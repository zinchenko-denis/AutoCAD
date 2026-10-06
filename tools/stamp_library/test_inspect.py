#!/usr/bin/env python3
"""Static contract + file-open doubles only. Does not evaluate AutoCAD behavior."""
from pathlib import Path
import re
import unittest


class String(str):
    pass


def parse(source):
    tokens = []
    pattern = re.compile(r'\s+|;[^\n]*|"(?:\\.|[^"\\])*"|[()\']|[^\s();\'"]+')
    end = 0
    for match in pattern.finditer(source):
        if match.start() != end:
            raise ValueError("Unparsed Lisp input")
        end = match.end()
        token = match.group()
        if token.isspace() or token.startswith(';'):
            continue
        tokens.append(String(token[1:-1]) if token.startswith('"') else token)
    if end != len(source):
        raise ValueError("Truncated Lisp input")
    position = 0

    def one():
        nonlocal position
        if position >= len(tokens):
            raise ValueError("Truncated expression")
        token = tokens[position]
        position += 1
        if type(token) is str and token == '(':
            items = []
            while position < len(tokens) and not (
                type(tokens[position]) is str and tokens[position] == ')'
            ):
                items.append(one())
            if position == len(tokens):
                raise ValueError("Unclosed list")
            position += 1
            return items
        if type(token) is str and token == ')':
            raise ValueError("Unexpected closing parenthesis")
        if type(token) is str and token == "'":
            return ['quote', one()]
        if type(token) is str and re.fullmatch(r'-?\d+', token):
            return int(token)
        return token

    result = []
    while position < len(tokens):
        result.append(one())
    return result


def walk(value):
    if isinstance(value, list):
        yield value
        for child in value:
            yield from walk(child)


SOURCE = Path(__file__).with_name('inspect.lsp').read_text(encoding='utf-8')
FORMS = parse(SOURCE)
FUNCTIONS = {f[1]: f for f in FORMS if isinstance(f, list) and f and f[0] == 'defun'}


class CaughtFailure:
    def __init__(self, message):
        self.message = message


class FileOpenDouble:
    """Run the actual atsi:open-new branching with fake filesystem primitives."""
    def __init__(self, path, exists=False, directory=False, lispsys=1, open_failure=None, open_nil=False):
        self.path, self.exists, self.directory = path, exists, directory
        self.lispsys, self.open_failure, self.open_nil = lispsys, open_failure, open_nil
        self.env = {'path': path, 'nil': None, 'T': True}
        self.opened = []
        self.messages = []

    def open_file(self, *arguments):
        self.opened.append(arguments)
        if self.open_failure:
            raise OSError(self.open_failure)
        return None if self.open_nil else 'FAKE_FILE'

    def evaluate(self, form):
        if isinstance(form, String):
            return str(form)
        if isinstance(form, int):
            return form
        if type(form) is str:
            return self.env[form]
        head, *args = form
        if head == 'quote':
            return args[0]
        if head == 'setq':
            for symbol, value in zip(args[::2], args[1::2]):
                self.env[symbol] = self.evaluate(value)
            return self.env[args[-2]]
        if head == 'cond':
            for clause in args:
                condition = self.evaluate(clause[0])
                if condition:
                    result = condition
                    for item in clause[1:]:
                        result = self.evaluate(item)
                    return result
            return None
        if head == 'or':
            for item in args:
                value = self.evaluate(item)
                if value:
                    return value
            return None
        values = [self.evaluate(item) for item in args]
        if head == 'list':
            return values
        if head == 'member':
            return values[0] in values[1]
        if head == 'not':
            return not values[0]
        if head == 'getvar':
            assert values[0] == 'LISPSYS'
            return self.lispsys
        if head == 'null':
            return values[0] is None
        if head in ('=', '/='):
            return (values[0] == values[1]) if head == '=' else (values[0] != values[1])
        if head == 'strcase':
            return values[0].upper()
        if head == 'vl-filename-extension':
            return Path(values[0]).suffix or None
        if head == 'findfile':
            return values[0] if self.exists else None
        if head == 'vl-file-directory-p':
            return self.directory
        if head == 'princ':
            self.messages.extend(values)
            return None
        if head == 'strcat':
            return ''.join(values)
        if head == 'vl-catch-all-apply':
            assert values[0] == 'open', 'Only the report open is mocked'
            try:
                return self.open_file(*values[1])
            except OSError as error:
                return CaughtFailure(str(error))
        if head == 'vl-catch-all-error-p':
            return isinstance(values[0], CaughtFailure)
        if head == 'vl-catch-all-error-message':
            return values[0].message
        raise AssertionError(f'Unsupported double operation: {head}')

    def run(self):
        return self.evaluate(FUNCTIONS['atsi:open-new'][3])


class InspectContract(unittest.TestCase):
    def test_structure_and_single_command(self):
        self.assertEqual([name for name in FUNCTIONS if name.startswith('c:')], ['c:ATFSTAMPINSPECT'])
        parsed = parse('(princ ")")')
        self.assertEqual(parsed, [['princ', String(')')]])
        self.assertIs(type(parsed[0][1]), String)

    def test_no_drawing_mutation_or_arbitrary_execution(self):
        allowed_external = {
            'princ', 'strcat', 'if', 'write-line', 'vl-prin1-to-string', 'cond', 'or', 'null',
            '=', '/=', 'strcase', 'vl-filename-extension', 'T', 'findfile', 'vl-file-directory-p',
            'open', 'type', 'quote', 'mapcar', 'vlax-variant-value', 'vlax-safearray->list',
            'foreach', 'setq', 'vl-catch-all-apply', 'list', 'and', 'not', 'vl-catch-all-error-p',
            'progn', 'vl-symbol-name', 'vl-catch-all-error-message', 'member', '1+', 'entget',
            'while', 'cdr', 'assoc', '<', 'cons', 'car', 'defun', 'getfiled', 'getvar', 'rtos',
            'nentselp', 'cadr', '>', 'length', 'caddr', 'cadddr', 'itoa', 'close', '*error*',
        }
        quoted_targets = {
            'vlax-property-available-p', 'vlax-get-property', 'atsi:unbox',
            'vlax-method-applicable-p', 'vlax-invoke-method', 'vlax-ename->vla-object',
            'vlax-dump-object', 'close', 'vl-load-com', 'open',
        }

        def audit(form):
            if not isinstance(form, list) or not form:
                return
            head = form[0]
            if head == 'quote':
                return
            if type(head) is str:
                self.assertTrue(head in allowed_external or head.startswith('atsi:'), head)
                if head == 'vl-catch-all-apply':
                    self.assertEqual(form[1][0], 'quote')
                    self.assertIn(form[1][1], quoted_targets)
                children = form[3:] if head == 'defun' else form[1:]
                if head == 'cond':
                    children = [x for clause in form[1:] for x in clause]
            else:
                children = form
            for child in children:
                audit(child)

        for form in FORMS:
            audit(form)
        method = FUNCTIONS['atsi:attributes'][3]
        self.assertEqual(method[:2], ['if', ['member', 'method', ['quote', ['GetAttributes', 'GetConstantAttributes']]]])
        self.assertEqual(sum(f[0] == 'open' for f in walk(FORMS) if f), 0)
        opens = [f for f in walk(FORMS) if f and f[0] == 'vl-catch-all-apply'
                 and f[1] == ['quote', 'open']]
        self.assertEqual(opens, [['vl-catch-all-apply', ['quote', 'open'],
                                 ['list', 'path', 'w', 'utf8-bom']]])

    def test_locals_are_declared(self):
        dynamic = {'atsi:log', 'atsi:com'}

        def audit(form, scope):
            if not isinstance(form, list) or not form or form[0] == 'quote':
                return
            head = form[0]
            if head == 'defun':
                scope = scope | set(form[2])
                children = form[3:]
            else:
                children = form[1:]
            written = form[1::2] if head == 'setq' else form[1:2] if head == 'foreach' else []
            for name in written:
                self.assertIn(name, scope | dynamic)
            for child in children:
                audit(child, scope)

        for form in FORMS:
            audit(form, set())

    def test_file_open_refuses_cancel_existing_directory_and_non_txt(self):
        for path, exists, directory in [(None, False, False), ('', False, False),
            ('report.txt', True, False), ('report.txt', False, True), ('drawing.dwg', False, False)]:
            double = FileOpenDouble(path, exists, directory)
            self.assertIsNone(double.run())
            self.assertEqual(double.opened, [])

    def test_unicode_engines_open_new_txt_explicitly_as_utf8_bom(self):
        for engine in (1, 2):
            double = FileOpenDouble('new_report.TXT', lispsys=engine)
            self.assertEqual(double.run(), 'FAKE_FILE')
            self.assertEqual(double.opened, [('new_report.TXT', 'w', 'utf8-bom')])

    def test_legacy_or_unknown_engine_never_opens_a_report(self):
        for engine in (0, 3, None):
            double = FileOpenDouble('new_report.txt', lispsys=engine)
            self.assertIsNone(double.run())
            self.assertEqual(double.opened, [])
            self.assertTrue(any('F2 only; UTF-8 TXT disabled' in x for x in double.messages))

    def test_caught_open_failure_and_nil_result_leave_f2_only(self):
        for options in ({'open_failure': 'permission denied'}, {'open_nil': True}):
            double = FileOpenDouble('new_report.txt', **options)
            self.assertIsNone(double.run())
            self.assertEqual(double.opened, [('new_report.txt', 'w', 'utf8-bom')])
            self.assertTrue(any('вывод только в F2' in x for x in double.messages))
            if options.get('open_failure'):
                self.assertIn('permission denied', ''.join(double.messages))


if __name__ == '__main__':
    unittest.main()
