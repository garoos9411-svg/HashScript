using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

// ============================================================================
//  ZipHelper — упаковка папки в ZIP «вручную» через ZipArchive.
//  Класс ZipFile.CreateFromDirectory формально доступен только с .NET 4.5,
//  а системный компилятор csc.exe (v4.0.30319) собирает код под C# 5 и не
//  всегда корректно подхватывает System.IO.Compression.FileSystem.dll.
//  Собственный обход надёжен на любой Windows 10/11 и дополнительно:
//   • пропускает временные файлы (_full_*.evtx), которые больше не нужны;
//   • сохраняет структуру папок внутри архива;
//   • устойчив к ошибкам отдельного файла (пропускаем его, не роняем сборку).
// ============================================================================
namespace ForensicCollector
{
    internal static class ZipHelper
    {
        /// <summary>Рекурсивно упаковывает содержимое sourceDir в ZIP-файл zipPath.</summary>
        public static void CreateFromDirectory(string sourceDir, string zipPath)
        {
            using (var fs = new FileStream(PathEx.Long(zipPath), FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                AddDirectoryRecursive(archive, sourceDir, sourceDir);
            }
        }

        private static void AddDirectoryRecursive(ZipArchive archive, string root, string dir)
        {
            // Файлы текущей папки
            foreach (string file in Directory.GetFiles(PathEx.Long(dir)))
            {
                string name = Path.GetFileName(file);
                if (name.StartsWith("_full_")) continue; // временные полные дампы журналов — не нужны в отчёте
                try
                {
                    // Относительный путь внутри архива (слэш «/» — стандарт ZIP)
                    string rel = file.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
                    ZipArchiveEntry entry = archive.CreateEntry(rel, CompressionLevel.Optimal);
                    using (Stream src = File.OpenRead(PathEx.Long(file)))
                    using (Stream dst = entry.Open())
                        src.CopyTo(dst);
                }
                catch
                {
                    // Один недоступный файл не должен срывать всю упаковку
                }
            }
            // Подпапки
            foreach (string sub in Directory.GetDirectories(PathEx.Long(dir)))
                AddDirectoryRecursive(archive, root, sub);
        }
    }
}
