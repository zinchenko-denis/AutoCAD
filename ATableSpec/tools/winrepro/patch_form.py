#!/usr/bin/env python3
# Готовит копию ReportBuilderForm.cs для прогона БЕЗ AutoCAD (mono/xvfb):
# вырезает AutoCAD-usings, глушит PickBlock, MessageBox -> ReproMsg (авто-ответ + лог).
import re, sys, os
root = os.path.join(os.path.dirname(__file__), '..', '..')
src = open(os.path.join(root, 'src', 'AtSpecPlugin', 'ReportBuilderForm.cs'), encoding='utf-8').read()
src = src.replace('using Autodesk.AutoCAD.EditorInput;\n', '')
src = re.sub(r'using Ac(Db|App) = [^\n]+\n', '', src)
endmark = 'finally { if (ui != null) ui.End(); }'
def cut(sig, stub):
    global src
    i = src.index(sig)
    j = src.index(endmark, i)
    j = src.index('}', j + len(endmark)) + 1   # закрывающая скобка МЕТОДА (после finally)
    src = src[:i] + sig + stub + src[j:]
cut('private bool PickBlock(out string layer, out string name, out Dictionary<string, string> attrs)', '''
        {
            layer = null; name = null;
            attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return false;   // репро: без AutoCAD пипетка недоступна
        }''')
cut('private string PickTableDef()', '''
        {
            return null;   // репро: без AutoCAD выбор таблицы недоступен
        }''')
src = src.replace('MessageBox.Show(', 'ReproMsg.Show(')
open(os.path.join(os.path.dirname(__file__), 'FormPatched.cs'), 'w', encoding='utf-8').write(src)
print('FormPatched.cs готов')
