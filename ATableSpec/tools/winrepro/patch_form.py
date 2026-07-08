#!/usr/bin/env python3
# Готовит копию ReportBuilderForm.cs для прогона БЕЗ AutoCAD (mono/xvfb):
# вырезает AutoCAD-usings, глушит PickBlock, MessageBox -> ReproMsg (авто-ответ + лог).
import re, sys, os
root = os.path.join(os.path.dirname(__file__), '..', '..')
src = open(os.path.join(root, 'src', 'AtSpecPlugin', 'ReportBuilderForm.cs'), encoding='utf-8').read()
src = src.replace('using Autodesk.AutoCAD.EditorInput;\n', '')
src = re.sub(r'using Ac(Db|App) = [^\n]+\n', '', src)
sig = 'private bool PickBlock(out string layer, out string name, out Dictionary<string, string> attrs)'
i = src.index(sig)
j = src.index('}', src.index('finally { if (ui != null) ui.End(); }', i)) + 1
src = src[:i] + sig + '''
        {
            layer = null; name = null;
            attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return false;   // репро: без AutoCAD пипетка недоступна
        }''' + src[j:]
src = src.replace('MessageBox.Show(', 'ReproMsg.Show(')
open(os.path.join(os.path.dirname(__file__), 'FormPatched.cs'), 'w', encoding='utf-8').write(src)
print('FormPatched.cs готов')
