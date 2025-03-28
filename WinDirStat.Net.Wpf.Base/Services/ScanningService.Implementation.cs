using Microsoft.Toolkit.HighPerformance.Buffers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.SqlClient;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Documents;
using WinDirStat.Net.Model.Extensions;
using WinDirStat.Net.Model.Files;
using WinDirStat.Net.Native;
using WinDirStat.Net.Utils;

namespace WinDirStat.Net.Services {
    partial class ScanningService {

        #region Private Classes

        private class ScanningState {
            public long TotalSize;
            public long FreeSpace;
            public string RootPath;
            public bool IsDrive;
            public string RecycleBinPath;
            public FolderItem Root;
        }

        #endregion

        #region Fields

        private List<ScanningState> scanningStates;

        #endregion

        public long GetCompressedFileSize(string filePath) {
            uint low = Win32.GetCompressedFileSize(filePath, out int high);
            if (low == Win32.InvalidFileSize) {
                int error = Marshal.GetLastWin32Error();
                if (error != 0)
                    throw new Win32Exception(error);
            }
            return ((long) high << 32) | low;
        }

        /// <summary>Runs the actual scan on any number of root paths.</summary>
        /// 
        /// <param name="rootPaths">The root paths to scan.</param>
        /// <param name="token">The token for cleanly cancelling the operations.</param>
        protected void Scan(string[] rootPaths, CancellationToken token) {
            // See if we can perform a single scan
            if (rootPaths.Length == 1) {
                Scan(rootPaths[0], token);
                return;
            }

            scanningStates = rootPaths.Select(p => CreateState(p, false)).ToList();
            CanDisplayProgress = !scanningStates.Any(s => !s.IsDrive);

            TotalSize = scanningStates.Sum(s => s.TotalSize);
            TotalFreeSpace = scanningStates.Sum(s => s.FreeSpace);

            // Computer Root
            RootItem computerRoot = new RootItem(this);
            foreach (ScanningState state in scanningStates) {
                computerRoot.AddItem(state.Root);
            }

            RootItem = computerRoot;
            ProgressState = ScanProgressState.Started;

            // Master file tables cannot be scanned inline, so scan them one at a time first
            for (int i = 0; i < scanningStates.Count; i++) {
                ScanningState state = scanningStates[i];
                try {
                    ScanMtf(state, token);
                    scanningStates.RemoveAt(i--);
                }
                catch (Exception) {
                    // We don't have permission, are not elevated, or path is not an NTFS drive
                    // We'll scan this path normally instead
                }
            }

            // Are there any leftover states to work on?
            if (scanningStates.Any())
                ScanNative(token);

            FinishScan(token);
        }

        /// <summary>Runs the actual scan on a single root path.</summary>
        /// 
        /// <param name="rootPath">The single root path to scan.</param>
        /// <param name="token">The token for cleanly cancelling the operations.</param>
        private void Scan(string rootPath, CancellationToken token) {
            if (Path.GetExtension(rootPath) == ".db") {
                // We are scanning a sqlite database
                ScanSqlite(rootPath, token);
                return;
            }
            ScanningState state = CreateState(rootPath, true);
            scanningStates = new List<ScanningState>() { state };
            CanDisplayProgress = scanningStates[0].IsDrive;

            TotalSize = state.TotalSize;
            TotalFreeSpace = state.FreeSpace;

            RootItem = (RootItem) state.Root;
            ProgressState = ScanProgressState.Started;

            try {
                ScanMtf(state, token);
            }
            catch (Exception) {
                // We don't have permission, are not elevated, or path is not an NTFS drive
                // We'll scan this path normally instead
                ScanNative(token);
            }
            FinishScan(token);
        }

        class SqliteScanFileInfo : IScanFileInfo {
            public string Name { get; init; }

            public string FullName { get; init; }

            public long Size { get; init; }

            public FileAttributes Attributes { get; init; }

            public DateTime CreationTimeUtc { get; init; }

            public DateTime LastAccessTimeUtc { get; init; }

            public DateTime LastWriteTimeUtc { get; init; }

            public bool IsDirectory { get; init; }

            public bool IsSymbolicLink { get; init; }
        }

        private void ScanSqlite(string rootPath, CancellationToken token) {
            var sqlConnection = new SQLiteConnection($"Data Source={rootPath};Version=3;");
            sqlConnection.Open();
            RootItem = new RootItem(this);
            ProgressState = ScanProgressState.Started;
            using (var comm = sqlConnection.CreateCommand()) {
                PopulateSqliteFolder(RootItem, null, comm);
            }
            FinishScan(token);
        }

        private void PopulateSqliteFolder(FolderItem folder, long? folderId, SQLiteCommand comm) {
            if (folderId.HasValue) {
                comm.CommandText = "SELECT id, name, is_directory, size, last_modified_ticks, last_accessed_ticks, expiry_ticks FROM files WHERE parent_id = @parentId";
                comm.Parameters.Add(new SQLiteParameter("@parentId", folderId.Value));
            }
            else {
                comm.CommandText = "SELECT id, name, is_directory, size, last_modified_ticks, last_accessed_ticks, expiry_ticks FROM files WHERE parent_id IS NULL";
            }
            var subFolders = new List<(FolderItem subfolder, long id)>();
            using (var reader = comm.ExecuteReader()) {
                while (reader.Read()) {
                    var id = reader.GetInt64(0);
                    var name = reader.GetString(1);
                    var isDir = reader.GetBoolean(2);
                    if (isDir) {
                        var subFolder = new FolderItem(new SqliteScanFileInfo() {
                            Name = name,
                            FullName = folder.FullName + "/" + name,
                            Size = reader.GetInt64(3),
                            LastWriteTimeUtc = DateTime.FromBinary(reader.GetInt64(4)),
                            LastAccessTimeUtc = reader.GetFieldAffinity(5) == TypeAffinity.Null ? DateTime.MinValue : DateTime.FromBinary(reader.GetInt64(5)),
                            CreationTimeUtc = reader.GetFieldAffinity(6) == TypeAffinity.Null ? DateTime.MinValue : DateTime.FromBinary(reader.GetInt64(6)),
                            IsDirectory = isDir,
                        });
                        folder.AddItem(subFolder);
                        subFolders.Add((subFolder, id));
                    }
                    else {
                        folder.AddItem(new FileItem(new SqliteScanFileInfo() {
                            Name = name,
                            FullName = folder.FullName + "/" + name,
                            Size = reader.GetInt64(3),
                            LastWriteTimeUtc = DateTime.FromBinary(reader.GetInt64(4)),
                            LastAccessTimeUtc = reader.GetFieldAffinity(5) == TypeAffinity.Null ? DateTime.MinValue : DateTime.FromBinary(reader.GetInt64(5)),
                            CreationTimeUtc = reader.GetFieldAffinity(6) == TypeAffinity.Null ? DateTime.MinValue : DateTime.FromBinary(reader.GetInt64(6)),
                            IsDirectory = isDir,
                        }, Extensions.GetOrAddFromPath(name)));
                    }
                }
            }
            foreach (var (subFolder, id) in subFolders) {
                PopulateSqliteFolder(subFolder, id, comm);
            }
        }

        /// <summary>Performs the final opreations after a scan.</summary>
        /// <param name="token"></param>
        private void FinishScan(CancellationToken token) {
            scanningStates = null;
            if (!token.IsCancellationRequested) {
                RootItem.Finish();
            }
        }

        /// <summary>Creates a <see cref="ScanningState"/> for the specified root path.</summary>
        /// 
        /// <param name="rootPath">The path to create a state for.</param>
        /// <param name="single">
        /// True if the there is only one state.<para/>
        /// This means this is the absolute root item instead of Computer.
        /// </param>
        /// <returns>The newly created and initialized <see cref="ScanningState"/>.</returns>
        private ScanningState CreateState(string rootPath, bool single) {
            rootPath = Path.GetFullPath(rootPath);
            string pathRoot = Path.GetPathRoot(rootPath);
            ScanningState state = new ScanningState {
                IsDrive = PathUtils.IsSamePath(pathRoot, rootPath),
                RecycleBinPath = Path.Combine(pathRoot, "$Recycle.Bin"),
                Root = new RootItem(this, new DirectoryInfo(rootPath), single),
                RootPath = rootPath.ToUpperInvariant(),
            };
            if (state.IsDrive) {
                Win32.GetDiskFreeSpaceEx(rootPath, out _, out ulong totalSize, out ulong freeSpace);
                state.TotalSize = (long) totalSize;
                state.FreeSpace = (long) freeSpace;
            }
            return state;
        }

        /// <summary>
        /// Creates a <see cref="ScanningState"/> for the specified <see cref="FolderItem"/>.
        /// </summary>
        /// 
        /// <param name="folder">The folder to create the state for.</param>
        /// <returns>The newly created and initialized <see cref="ScanningState"/>.</returns>
        private ScanningState CreateState(FolderItem folder) {
            string rootPath = folder.FullName;
            string pathRoot = Path.GetPathRoot(rootPath);
            ScanningState state = new ScanningState {
                IsDrive = PathUtils.IsSamePath(pathRoot, rootPath),
                RecycleBinPath = Path.Combine(pathRoot, "$Recycle.Bin"),
                Root = folder,
                RootPath = rootPath.ToUpperInvariant(),
            };
            if (state.IsDrive) {
                Win32.GetDiskFreeSpaceEx(rootPath, out _, out ulong totalSize, out ulong freeSpace);
                state.TotalSize = (long) totalSize;
                state.FreeSpace = (long) freeSpace;
            }
            return state;
        }

        /// <summary>Checks if we should ignore this file in the scan.</summary>
        /// 
        /// <param name="state">The scan state for this file.</param>
        /// <param name="name">The name of the file.</param>
        /// <param name="path">The full path of the file.</param>
        /// <returns>True if the file should be skipped.</returns>
        private bool SkipFile(ScanningState state, string name, string path) {
            Debug.Assert(name.Length > 0);

            // We still want to see all those delicious files that were thrown away
            if (name[0] == '$' && !path.StartsWith(state.RecycleBinPath))
                return true;

            // Certified spam
            //if (string.Compare(name, "desktop.ini", true) == 0)
            //	return true;

            return false;
        }

        protected void FinishedCleanup() {
            scanningStates = null;
        }

        protected void ClosedCleanup() {
        }
    }
}
