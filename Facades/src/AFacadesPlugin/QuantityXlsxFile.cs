using System;
using System.Collections.Generic;
using System.IO;

namespace AFacadesPlugin
{
    // Файл готовится рядом с назначением. Esc до создания этого объекта
    // не затрагивает файл; ошибка записи не оставляет обрезанный XLSX.
    internal sealed class QuantityXlsxFile : IDisposable
    {
        private readonly string _path, _temporary, _backup;
        private bool _published, _complete, _hadFile;
        internal QuantityXlsxFile(string path, List<object[]> rows)
            : this(path, "Облицовка", rows) { }

        internal QuantityXlsxFile(string path, string sheetName, List<object[]> rows)
        {
            _path = Path.GetFullPath(path);
            string token = Guid.NewGuid().ToString("N");
            _temporary = _path + "." + token + ".tmp";
            _backup = _path + "." + token + ".bak";
            try { XlsxWriter.WriteExact(_temporary, sheetName, rows); }
            catch { Delete(_temporary); throw; }
        }
        internal void Publish()
        {
            _hadFile = File.Exists(_path);
            if (_hadFile) File.Replace(_temporary, _path, _backup);
            else File.Move(_temporary, _path);
            _published = true;
        }
        internal void Complete() { _complete = true; Delete(_backup); }
        public void Dispose()
        {
            if (_published && !_complete)
            {
                if (_hadFile) File.Replace(_backup, _path, null);
                else File.Delete(_path);
            }
            Delete(_temporary);
            if (_complete) Delete(_backup);
        }
        private static void Delete(string path)
        { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    }
}
