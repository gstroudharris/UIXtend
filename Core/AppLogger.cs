// Copyright (C) 2026  Grant Harris
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;

namespace UIXtend.Core
{
    /// <summary>
    /// Lightweight session logger. Writes timestamped lines to a .txt file under the
    /// project's "logs" folder. Thread-safe; AutoFlush ensures nothing is lost on crash.
    /// </summary>
    public static class AppLogger
    {
        private static StreamWriter? _writer;
        private static readonly object _lock = new();

        public static void Initialize()
        {
            var logsDir = Path.Combine(FindProjectRoot(), "logs");
            Directory.CreateDirectory(logsDir);

            var fileName = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt";
            var filePath = Path.Combine(logsDir, fileName);

            _writer = new StreamWriter(filePath, append: false, System.Text.Encoding.UTF8)
            {
                AutoFlush = true
            };

            Log("=== Session started ===");
            Log($"Log file: {filePath}");
        }

        public static void Log(string message)
        {
            if (_writer == null) return;   // logging not enabled — skip everything
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
            lock (_lock)
                _writer.WriteLine(line);
            System.Diagnostics.Debug.WriteLine($"[UIXtend] {line}");
        }

        public static void LogException(string context, Exception ex)
        {
            Log($"ERROR [{context}] {ex.GetType().Name}: {ex.Message}");
            Log($"  StackTrace: {ex.StackTrace?.Replace(Environment.NewLine, Environment.NewLine + "  ")}");
        }

        public static void Dispose()
        {
            lock (_lock)
            {
                Log("=== Session ended ===");
                _writer?.Dispose();
                _writer = null;
            }
        }

        /// <summary>
        /// Walks up from the executable directory until a .csproj file is found.
        /// Falls back to AppContext.BaseDirectory when deployed (no .csproj present).
        /// </summary>
        private static string FindProjectRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (dir.GetFiles("*.csproj").Length > 0)
                    return dir.FullName;
                dir = dir.Parent!;
            }
            return AppContext.BaseDirectory;
        }
    }
}
