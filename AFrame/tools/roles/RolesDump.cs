// Синтетика «в ролях конструктора» (29.09, Денис: «прогоняем синтетические тесты на поиск ошибок в
// разных ролях конструктора»). Применяет к FrameSettings действия роли так, как их сделал бы
// человек в окне ATFRAME (поля по порядку, смена типа SetSubType, марка шины RailBrand), и выдаёт
// для каждой роли: проверку окна (Validate), запрос движку (EngineParams — тот же метод, что в
// команде ATFRAME), круг метки подсистемы (ToDict → FromDict → тот же запрос) и описание «Что
// будет». Действие "SNAPSHOT" — снимок запроса посреди сценария (повторный ATFRAME с другими
// настройками). Роли и проверки — tools/roles_synth.py (корень репо).
// Сборка (из AFrame):
//   mcs -out:/tmp/roles.exe -r:System.Web.Extensions.dll tools/roles/RolesDump.cs src/AFramePlugin/FrameSettings.cs
// Запуск: mono /tmp/roles.exe roles_in.json roles_out.json
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using AFramePlugin;

static class RolesDump
{
    static void Apply(FrameSettings fs, string name, object v)
    {
        if (name == "SetSubType") { fs.SetSubType(Convert.ToString(v, CultureInfo.InvariantCulture)); return; }
        if (name == "RailBrand") { fs.RailBrand = Convert.ToString(v, CultureInfo.InvariantCulture); return; }
        var f = typeof(FrameSettings).GetField(name);
        if (f == null) throw new Exception("у FrameSettings нет поля " + name);
        f.SetValue(fs, Convert.ChangeType(v, f.FieldType, CultureInfo.InvariantCulture));
    }

    static int Main(string[] args)
    {
        var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var roles = ser.DeserializeObject(File.ReadAllText(args[0], Encoding.UTF8)) as object[];
        var outList = new List<object>();
        foreach (var ro in roles)
        {
            var r = ro as Dictionary<string, object>;
            var fs = new FrameSettings();
            bool hasLayout = Convert.ToBoolean(r["has_layout"]);
            int floors = Convert.ToInt32(r["floors_picked"]);
            var snaps = new List<object>();
            string fail = null;
            try
            {
                foreach (var ao in (object[])r["actions"])
                {
                    var pair = (object[])ao;
                    string an = Convert.ToString(pair[0], CultureInfo.InvariantCulture);
                    if (an == "SNAPSHOT")
                    {
                        snaps.Add(new Dictionary<string, object>
                        {
                            { "params", fs.EngineParams(floors) }, { "settings", fs.ToDict() },
                            { "valid", fs.Validate(hasLayout) },
                        });
                        continue;
                    }
                    Apply(fs, an, pair.Length > 1 ? pair[1] : null);
                }
            }
            catch (Exception ex) { fail = ex.Message; }
            var ep = fs.EngineParams(floors);
            var back = FrameSettings.FromDict(ser.DeserializeObject(ser.Serialize(fs.ToDict()))
                                               as Dictionary<string, object>);
            outList.Add(new Dictionary<string, object>
            {
                { "name", r["name"] },
                { "valid", fs.Validate(hasLayout) },
                { "params", ep },
                { "settings", fs.ToDict() },
                { "roundtrip", ser.Serialize(back.EngineParams(floors)) == ser.Serialize(ep) },
                { "describe", fs.Describe(hasLayout) },
                { "snapshots", snaps },
                { "apply_error", fail },
            });
        }
        File.WriteAllText(args[1], ser.Serialize(outList), new UTF8Encoding(false));
        return 0;
    }
}
