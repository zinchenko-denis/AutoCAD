using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AFramePlugin
{
    // The actual catalogue controls are shared with the local 2B1 editor.
    // These forms return detached intent; commands alone perform CAD writes.
    public sealed class FrameProjectForm : FrameSolutionForm
    {
        private readonly FrameProjectParameters _previous;
        private FrameProjectParameters _result;

        public FrameProjectForm(FrameProjectParameters current)
            : base(current == null ? null : current.defaults, false, ProjectOptions(current))
        { _previous = current == null ? null : current.Clone(); }

        public new FrameProjectParameters Result
        {
            get
            {
                if (base.Result == null) return null;
                if (_result == null) _result = FrameProjectParameters.CreateNext(_previous, base.Result);
                return _result.Clone();
            }
        }

        private static FrameSolutionEditorContext ProjectOptions(FrameProjectParameters current)
        {
            var previous = current == null ? null : current.Clone();
            return new FrameSolutionEditorContext
            {
                Title = "ATFPROJECT — параметры проекта",
                Instruction = "Общие значения этого DWG. Новые и прежние зоны не получают их автоматически: привязка задаётся отдельно через ATFZONEPARAMS.",
                ScopeCaption = previous == null ? "Проект ещё не создан." : "Проект " + previous.project_id + ", редакция " + previous.revision + ".",
                HideClear = true,
                SaveText = "Сохранить проект",
                Review = delegate(FrameSolutionSelection candidate, HashSet<string> overrides, bool clear)
                {
                    var proposed = FrameProjectParameters.CreateNext(previous, candidate);
                    var sb = new StringBuilder();
                    sb.AppendLine(previous == null ? "Создание параметров проекта" : "Изменения параметров проекта");
                    bool changed = FrameProjectForms.AppendChanges(sb, previous == null ? null : previous.defaults, candidate, null, null);
                    sb.AppendLine();
                    if (previous == null) sb.AppendLine("Будет создана редакция 1. Ни одна зона не будет привязана автоматически.");
                    else sb.AppendLine(changed ? "Редакция проекта: " + previous.revision + " → " + proposed.revision + "." :
                        "Значения не изменились. Редакция проекта останется " + previous.revision + ".");
                    if (previous != null && changed)
                        sb.AppendLine("Сохранённые результаты, зависящие от проекта, станут неактуальными. Для них понадобится полное построение ATFRAME. Режим «только кляммеры» не обновляет зависимость.");
                    sb.AppendLine("Число зависимых зон здесь не подсчитывается: полный обход чертежа не выполняется. Непривязанные зоны сохраняют свой режим.");
                    sb.AppendLine("Сохранение меняет параметры проекта; геометрия и ведомости автоматически не перестраиваются.");
                    sb.AppendLine(FrameProjectForms.EngineeringLimits);
                    return sb.ToString();
                }
            };
        }
    }

    public sealed class FrameZoneParametersForm : FrameSolutionForm
    {
        private readonly FrameProjectParameters _project;
        private readonly FrameZoneParameters _previous;
        private FrameZoneParameters _result;

        public FrameZoneParametersForm(FrameProjectParameters project, FrameZoneParameters binding, string scopeCaption,
            int zoneCount = 1, bool generationDependencyChanged = false)
            : base(FrameProjectForms.Effective(project, binding), false, ZoneOptions(project, binding, scopeCaption, zoneCount, generationDependencyChanged))
        { _project = project.Clone(); _previous = binding == null ? null : binding.Clone(); }

        public new FrameZoneParameters Result
        {
            get
            {
                if (WasCleared || base.Result == null) return null;
                if (_result == null) _result = FrameZoneParameters.CreateNext(_previous, _project,
                    FrameProjectForms.MakeOverrides(_project, _previous, base.Result, AcceptedOverridePaths));
                return _result.Clone();
            }
        }

        private static FrameSolutionEditorContext ZoneOptions(FrameProjectParameters project, FrameZoneParameters binding, string scope,
            int zoneCount, bool generationDependencyChanged)
        {
            var defaults = project.Clone(); var previous = binding == null ? null : binding.Clone();
            var options = new FrameSolutionEditorContext
            {
                Title = "ATFZONEPARAMS — параметры зон",
                Instruction = "Источник и узел заданы проектом. Для своих значений включите флажок у нужного поля. Без флажка поле наследуется из проекта.",
                ScopeCaption = scope + ". Проект " + defaults.project_id + ", редакция " + defaults.revision + ". " +
                    (previous == null ? "Привязка ещё не сохранена." : zoneCount > 1 ? "Редакция привязки своя у каждой зоны." : "Привязка зоны, редакция " + previous.revision + "."),
                LockIdentity = true, OverrideMode = true, ProjectDefaults = defaults.defaults,
                ClearText = "Снять привязку", HideClear = previous == null, SaveText = "Сохранить зоны"
            };
            foreach (var field in FrameParameterResolver.Fields)
            {
                options.Origins.Add(field.Path, "Проект, редакция " + defaults.revision);
                if (previous != null && previous.overrides.ContainsKey(field.Path)) options.OverridePaths.Add(field.Path);
            }
            options.Review = delegate(FrameSolutionSelection candidate, HashSet<string> paths, bool clear)
            {
                var sb = new StringBuilder(); sb.AppendLine(scope); sb.AppendLine();
                if (clear)
                {
                    sb.AppendLine("Снятие привязки к параметрам проекта");
                    sb.AppendLine("Привязка будет снята. Ранее созданный результат проекта станет неактуальным. Для следующего полного построения явно выберите локальное решение либо используйте режим без каталога.");
                    sb.AppendLine("Последние унаследованные значения не станут локальным решением автоматически. Параметры проекта не изменяются.");
                }
                else
                {
                    var proposed = FrameZoneParameters.CreateNext(previous, defaults,
                        FrameProjectForms.MakeOverrides(defaults, previous, candidate, paths));
                    sb.AppendLine(previous == null ? "Явная привязка выбранных зон к проекту" : "Изменения параметров выбранных зон");
                    bool changed = FrameProjectForms.AppendChanges(sb, FrameProjectForms.Effective(defaults, previous), candidate,
                        previous == null ? new Dictionary<string, FrameParameterOverride>() : previous.overrides, proposed.overrides);
                    if (previous == null) sb.AppendLine("Привязка будет создана явно. Все поля без своих значений будут наследоваться.");
                    else if (zoneCount > 1) sb.AppendLine(changed ? "Редакция каждой изменённой привязки будет увеличена отдельно. Общая редакция группы не назначается." :
                        "Значения и происхождение не изменились. Редакция каждой привязки сохраняется.");
                    else sb.AppendLine(changed ? "Редакция привязки: " + previous.revision + " → " + proposed.revision + "." :
                        "Значения и происхождение не изменились. Редакция привязки останется " + previous.revision + ".");
                    if (previous == null || changed || generationDependencyChanged)
                        sb.AppendLine("Ранее созданные результаты выбранных зон станут неактуальными. Для обновления требуется полное построение ATFRAME; геометрия сейчас не меняется.");
                    sb.AppendLine("Параметры проекта и других зон не изменяются.");
                }
                sb.AppendLine(); sb.AppendLine(FrameProjectForms.EngineeringLimits);
                return sb.ToString();
            };
            return options;
        }
    }

    public static class FrameProjectForms
    {
        internal const string EngineeringLimits = "Это декларация параметров. Совместимость сборки, сечения, крепления, стыки и прочность не подтверждены. Автоматический подбор кронштейна недоступен.";
        private static readonly string PreviewDigest = new string('0', 64);

        internal static FrameSolutionSelection Effective(FrameProjectParameters project, FrameZoneParameters binding)
        {
            if (project == null) throw new FrameSolutionSelectionException("Сначала задайте параметры проекта командой ATFPROJECT.");
            if (binding == null) return project.defaults.Clone();
            return FrameParameterResolver.Resolve(project, PreviewDigest, binding, PreviewDigest, "preview", "preview").Selection;
        }

        internal static Dictionary<string, FrameParameterOverride> MakeOverrides(FrameProjectParameters project,
            FrameZoneParameters previous, FrameSolutionSelection candidate, HashSet<string> paths)
        {
            var result = new Dictionary<string, FrameParameterOverride>(StringComparer.Ordinal);
            var before = Effective(project, previous);
            foreach (var field in FrameParameterResolver.Fields)
            {
                if (!paths.Contains(field.Path)) continue;
                object value = FrameParameterResolver.GetValue(candidate, field.Path);
                FrameParameterOverride old;
                if (previous != null && previous.overrides.TryGetValue(field.Path, out old) &&
                    ValueText(value) == ValueText(FrameParameterResolver.GetValue(before, field.Path)))
                    result.Add(field.Path, FrameParameterOverride.FromDict(old.ToDict()));
                else result.Add(field.Path, field.CanClear && IsEmpty(value) ? FrameParameterOverride.Clear() : FrameParameterOverride.Set(value));
            }
            return result;
        }

        internal static bool AppendChanges(StringBuilder text, FrameSolutionSelection previous, FrameSolutionSelection next,
            Dictionary<string, FrameParameterOverride> previousOverrides, Dictionary<string, FrameParameterOverride> nextOverrides)
        {
            bool changed = false;
            foreach (var field in FrameParameterResolver.Fields)
            {
                string oldValue = previous == null ? "не задано" : ValueText(FrameParameterResolver.GetValue(previous, field.Path));
                string newValue = ValueText(FrameParameterResolver.GetValue(next, field.Path));
                string oldOrigin = Origin(previousOverrides, field.Path), newOrigin = Origin(nextOverrides, field.Path);
                if (previous == null || oldValue != newValue || oldOrigin != newOrigin)
                {
                    changed = true;
                    text.AppendLine(field.Title + ": " + oldValue + oldOrigin + " → " + newValue + newOrigin);
                }
            }
            if (!changed) text.AppendLine("Изменений полей нет.");
            return changed;
        }

        private static string Origin(Dictionary<string, FrameParameterOverride> overrides, string path)
        {
            if (overrides == null) return "";
            FrameParameterOverride item;
            return !overrides.TryGetValue(path, out item) ? " [из проекта]" : item.state == "clear" ? " [в зоне не задано]" : " [своё значение]";
        }
        private static bool IsEmpty(object value)
        {
            if (value == null) return true;
            var values = value as IEnumerable;
            if (values == null || value is string) return false;
            foreach (var ignored in values) return false;
            return true;
        }
        internal static string ValueText(object value)
        {
            if (value == null) return "не задано";
            if (value is string) return (string)value;
            if (value is IEnumerable)
            {
                var parts = new List<string>(); foreach (object item in (IEnumerable)value) parts.Add(ValueText(item));
                return parts.Count == 0 ? "не задано" : string.Join("; ", parts.ToArray());
            }
            return Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
        }

        public static FrameSolutionEditorContext ReadOnlyContext(IEnumerable<FrameParameterContext> contexts)
        {
            var saved = new List<FrameParameterContext>();
            foreach (var item in contexts) saved.Add(item.Clone());
            if (saved.Count == 0) throw new FrameSolutionSelectionException("Не передано происхождение параметров выбранных зон.");
            var first = saved[0];
            foreach (var item in saved)
                if (item.project.project_id != first.project.project_id || item.project.revision != first.project.revision ||
                    item.project.content_digest != first.project.content_digest || item.effective_digest != first.effective_digest)
                    throw new FrameSolutionSelectionException("Для общего просмотра требуются одна редакция проекта и одинаковые итоговые значения зон.");
            var result = new FrameSolutionEditorContext
            {
                Title = "ATFRAME — параметры из проекта",
                Instruction = "Параметры привязанных зон доступны для просмотра. Изменяйте проект через ATFPROJECT, привязку и свои значения зон — через ATFZONEPARAMS.",
                ReadOnlyReason = "Просмотр. ATFRAME не изменяет параметры проекта или привязку зон.",
                ScopeCaption = "Проект " + first.project.project_id + ", редакция " + first.project.revision + ". " +
                    (saved.Count == 1 ? "Зона: " + first.zone.zone_id + "." : "Выбрано зон: " + saved.Count + "."),
                LockIdentity = true, HideClear = true
            };
            foreach (var field in FrameParameterResolver.Fields)
            {
                int inherited = 0, local = 0, clear = 0;
                foreach (var context in saved)
                {
                    string origin = context.origins[field.Path];
                    if (origin == "project_default") inherited++;
                    else if (origin == "zone_clear") clear++;
                    else local++;
                }
                string caption = saved.Count == 1 ? inherited == 1 ? "Проект, редакция " + first.project.revision :
                    clear == 1 ? "В зоне не задано" : "Зона: своё значение" :
                    "Зоны: из проекта " + inherited + ", своё " + local + ", не задано " + clear;
                result.Origins.Add(field.Path, caption);
            }
            return result;
        }
    }
}
