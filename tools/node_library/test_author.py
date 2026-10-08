#!/usr/bin/env python3
"""Contract, Lisp scope and cleanup-policy tests. NO AutoCAD evaluator here."""
from __future__ import annotations

import copy
import json
import math
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
from generate import ContractError, lisp, render, validate

ROOT = Path(__file__).resolve().parent


class String(str):
    """Distinguish AutoLISP string literals from symbols in the test parser."""


def parse(source):
    tokens, i = [], 0
    while i < len(source):
        c = source[i]
        if c.isspace() or c == "\ufeff":
            i += 1
        elif c == ";":
            end = source.find("\n", i)
            i = len(source) if end < 0 else end + 1
        elif c in "()'":
            tokens.append(c)
            i += 1
        elif c == '"':
            i += 1
            value = ""
            while i < len(source) and source[i] != '"':
                if source[i] == "\\":
                    i += 1
                    if i >= len(source):
                        raise ValueError("unterminated string escape")
                    value += {"n": "\n", "r": "\r", "t": "\t"}.get(source[i], source[i])
                else:
                    value += source[i]
                i += 1
            if i == len(source):
                raise ValueError("unterminated string")
            tokens.append(String(value))
            i += 1
        else:
            start = i
            while i < len(source) and not source[i].isspace() and source[i] not in "();'\"":
                i += 1
            token = source[start:i]
            try:
                token = float(token) if any(c in token for c in ".eE") else int(token)
            except ValueError:
                pass
            tokens.append(token)
    cursor = 0

    def one():
        nonlocal cursor
        if cursor >= len(tokens):
            raise ValueError("truncated expression")
        token = tokens[cursor]
        cursor += 1
        if type(token) is str and token == "(":
            result = []
            while cursor < len(tokens) and not (type(tokens[cursor]) is str and tokens[cursor] == ")"):
                result.append(one())
            if cursor == len(tokens):
                raise ValueError("unclosed list")
            cursor += 1
            return result
        if type(token) is str and token == ")":
            raise ValueError("unexpected closing parenthesis")
        if type(token) is str and token == "'":
            return ["quote", one()]
        return token

    forms = []
    while cursor < len(tokens):
        forms.append(one())
    return forms


def audit_scope(forms):
    """Find a write to a caller's dynamically scoped variable before shipping.

    Only the afn:-prefixed bindings listed here cross helper boundaries:
    input data, diagnostics and the command-owned editor flag.
    Nested *error* deliberately captures the enclosing command's declared locals.
    """
    globals_allowed = {"afn:data", "afn:failure", "afn:editor-owned", "afn:stage", "afn:last-command", "afn:failed-prompt"}
    errors = []

    def body(form, scope):
        if not isinstance(form, list) or not form:
            return
        head = form[0]
        if head == "quote":
            return
        if head == "defun":
            locals_ = {x for x in form[2] if x != "/"}
            for child in form[3:]:
                body(child, scope | locals_)
            return
        if head == "lambda":
            for child in form[2:]:
                body(child, scope | set(form[1]))
            return
        writes = form[1::2] if head == "setq" else form[1:2] if head == "foreach" else []
        for symbol in writes:
            if symbol not in scope | globals_allowed:
                errors.append(symbol)
        for child in form[1:]:
            body(child, scope)

    for form in forms:
        body(form, set())
    return errors


class NativeFailure(Exception):
    pass


class Pair(tuple):
    """Dotted alist cell, distinct from a proper Lisp list."""


class CleanupModel:
    """Execute only the error-handler control flow with mocked CAD operations.

    This proves ownership decisions in our handler, not AutoCAD UNDO/BCLOSE.
    """
    def __init__(self, editor, owned_editor, commands_owned, undo_state, fail=None, active=0):
        self.vars = {"BLOCKEDITOR": editor, "CMDACTIVE": active, "LASTPROMPT": "failed native prompt",
                     "UNDOCTL": 61 if undo_state == "open" else 53}
        self.env = {"afn:editor-owned": owned_editor, "commands-owned": commands_owned,
                    "undo-state": undo_state, "settings": [], "afn:failure": None, "message": "test",
                    "settings-changed": commands_owned,
                    "afn:geometry-started": commands_owned,
                    "afn:stage": "test stage", "afn:last-command": "_.BPARAMETER", "name": "AFN_TEST"}
        self.owned_definition = commands_owned
        self.calls = []
        self.messages = []
        self.fail = fail

    def run(self, expression):
        if isinstance(expression, String) or isinstance(expression, (int, float)):
            return expression
        if isinstance(expression, str):
            return True if expression == "T" else None if expression == "nil" else self.env.get(expression)
        if not expression:
            return None
        head, *args = expression
        if head == "quote":
            return args[0]
        if head == "if":
            return self.run(args[1]) if self.run(args[0]) else self.run(args[2]) if len(args) > 2 else None
        if head == "progn":
            result = None
            for arg in args:
                result = self.run(arg)
            return result
        if head == "and":
            return all(self.run(x) for x in args)
        if head == "or":
            return any(self.run(x) for x in args)
        if head == "setq":
            for key, value in zip(args[::2], args[1::2]):
                self.env[key] = self.run(value)
            return self.env[key]
        if head == "repeat":
            for _ in range(self.run(args[0])):
                for arg in args[1:]:
                    self.run(arg)
            return None
        values = [self.run(x) for x in args]
        if head in ("equal", "="):
            return values[0] == values[1]
        if head == "/=":
            return values[0] != values[1]
        if head == "not":
            return not values[0]
        if head == "logand":
            return values[0] & values[1]
        if head == "member":
            return values[0] in values[1]
        if head == "null":
            return values[0] is None
        if head == "itoa":
            return str(values[0])
        if head == "getvar":
            return self.vars[values[0]]
        if head == "tblsearch":
            return {"name": values[1]} if self.owned_definition else None
        if head == "list":
            return values
        if head == "strcat":
            return "".join(values)
        if head == "princ":
            self.messages.extend(values)
            return None
        if head == "command":
            raise NativeFailure("command is forbidden in local *error* context")
        if head == "afn:restore":
            self.calls.append(("restore",))
            return None
        if head == "vl-catch-all-error-p":
            return isinstance(values[0], NativeFailure)
        if head == "vl-catch-all-error-message":
            return str(values[0])
        if head == "exit":
            raise NativeFailure(self.env.get("afn:failure"))
        if head == "vl-catch-all-apply":
            fn, tokens = values
            if fn == "afn:restore":
                self.calls.append(("restore",))
                return NativeFailure("restore failed") if self.fail == "restore" else None
            if fn != "command-s":
                raise AssertionError(fn)
            if not tokens:
                raise AssertionError("no-argument command-s has no documented main-command cancellation")
            call = tuple(str(x) for x in tokens)
            self.calls.append(call)
            self.vars["LASTPROMPT"] = "cleanup prompt: " + call[0]
            if self.fail == call[0]:
                return NativeFailure(call[0])
            if call[0] == "_.BCLOSE" and self.fail != "close-noop":
                self.vars["BLOCKEDITOR"] = 0
            if call == ("_.UNDO", "_End") and self.fail != "end-noop":
                self.vars["UNDOCTL"] &= ~8
            return None
        raise AssertionError(f"unmodelled error-handler expression: {head}")


class PreflightModel(CleanupModel):
    """Run actual pre-build Lisp control flow; CAD state/commands are doubles."""
    def __init__(self, runtime=None):
        super().__init__(0, False, False, None)
        self.env.update({":vlax-true": -1, ":vlax-false": 0, "layer-zero": object()})
        self.layer = {"LayerOn": -1, "Freeze": 0, "Lock": 0}
        self.vars.update(UNDOCTL=53, DBMOD=0, DWGTITLED=0, BLOCKEDITOR=0, TILEMODE=1,
                         LISPSYS=1, CMDACTIVE=0)
        self.env["afn:data"] = {"block_name": "AFN_TEST"}
        self.defuns = {f[1]: f for f in parse(runtime or "") if isinstance(f, list) and f[:1] == ["defun"]}
        self.count, self.existing_block = 0, None
        self.owned_definition = False
        self.output, self.existing_file = "new-library.dwg", None
        self.dialog, self.reject_normalize, self.normalize_dbmod = None, False, None
        self.reject_begin, self.reject_end = False, False
        self.events = []

    def prepare(self):
        """Execute the real command up to its first geometry-writing helper."""
        for expression in self.defuns["c:ATFNATIVEBUILD"][3:]:
            if expression[:1] == ["defun"]:
                continue
            if expression[:1] == ["afn:build-definition"]:
                self.events.append("geometry reached")
                return
            self.run(expression)
        raise AssertionError("geometry boundary missing")

    def native_command(self, tokens):
        call = tuple(tokens)
        self.calls.append(call)
        self.events.append(call)
        flags = self.vars["UNDOCTL"]
        if self.fail == "normalize":
            self.vars["CMDACTIVE"] = 1
            raise NativeFailure("native normalization failed")
        if call == ("_.UNDO", "_All"):
            if flags & 1 or flags & 8:
                raise AssertionError("Off requires direct All and no active group")
        elif call == ("_.UNDO", "_Control", "_All"):
            if not (flags & 1 and flags & 2) or flags & 8:
                raise AssertionError("One requires Control/All and no active group")
        elif call == ("_.UNDO", "_Begin"):
            if flags & 11 != 1:
                raise AssertionError("Begin before verified All/no group")
            if not self.reject_begin:
                self.vars["UNDOCTL"] |= 8
            return
        elif call == ("_.UNDO", "_End"):
            if not self.reject_end:
                self.vars["UNDOCTL"] &= ~8
            return
        else:
            raise AssertionError(f"unexpected native preparation command: {call}")
        if not self.reject_normalize:
            self.vars["UNDOCTL"] = (flags | 1) & ~2
        if self.normalize_dbmod is not None:
            self.vars["DBMOD"] = self.normalize_dbmod

    def run(self, expression):
        if isinstance(expression, list) and expression:
            head, *args = expression
            if head == "vl-catch-all-apply" and self.run(args[0]) == "command-s":
                tokens = self.run(args[1])
                try:
                    return self.native_command(tokens)
                except NativeFailure as exc:
                    return exc
            if expression[0] == "afn:assert":
                if not self.run(expression[1]):
                    raise NativeFailure(self.run(expression[2]))
                return None
            if expression[0] in ("vla-get-LayerOn", "vla-get-Freeze", "vla-get-Lock"):
                return self.layer[expression[0][8:]]
            if head == "foreach":
                for item in self.run(args[1]):
                    self.env[args[0]] = item
                    for body in args[2:]:
                        self.run(body)
                return None
            if head in self.defuns and head != "afn:restore":
                form = self.defuns[head]
                signature = form[2]
                split = signature.index("/") if "/" in signature else len(signature)
                values = [self.run(a) for a in args]
                scoped = [s for s in signature if s != "/"]
                old = {s: self.env.get(s) for s in scoped}
                self.env.update(dict.fromkeys(scoped))
                self.env.update(zip(signature[:split], values))
                try:
                    result = None
                    for body in form[3:]:
                        result = self.run(body)
                    return result
                finally:
                    self.env.update(old)
            if head in ("afn:get", "vla-get-ActiveDocument", "vlax-get-acad-object", "vla-get-ModelSpace",
                        "vla-get-Layers", "vla-Item", "vla-get-Count", "tblsearch", "getfiled", "findfile",
                        "getvar", "setvar", "cons", "car", "cdr", "cadr", "assoc", "apply"):
                values = [self.run(a) for a in args]
                if head == "afn:get":
                    return values[1].get(values[0])
                if head in ("vla-get-ActiveDocument", "vlax-get-acad-object", "vla-get-ModelSpace", "vla-get-Layers", "vla-Item"):
                    return head
                if head == "vla-get-Count":
                    return self.count
                if head == "tblsearch":
                    return self.existing_block or ({"name": values[1]} if self.owned_definition else None)
                if head == "findfile":
                    return self.existing_file
                if head == "getfiled":
                    self.events.append("output dialog")
                    if self.dialog:
                        self.dialog(self)
                    return self.output
                if head == "getvar":
                    return self.vars.get(values[0], 0)
                if head == "setvar":
                    self.calls.append(("setvar", *values))
                    self.vars[values[0]] = values[1]
                    return values[1]
                if head == "cons":
                    return [values[0], *(values[1] or [])] if values[1] is None or isinstance(values[1], list) else Pair(values)
                if head == "assoc":
                    return Pair((values[0], values[1][values[0]])) if values[0] in values[1] else None
                if head == "car":
                    return values[0][0]
                if head == "cadr":
                    return values[0][1]
                if head == "cdr":
                    value = values[0]
                    return value[1] if isinstance(value, Pair) else value[2] if len(value) == 3 and value[1] == "." else value[1:]
                if head == "apply":
                    # Retained only to replay the historical runtime against
                    # the new protocol checks; shipped runtime uses command-s.
                    if values[0] != "vl-cmdf":
                        raise AssertionError(values[0])
                    return self.native_command(values[1])
        return super().run(expression)


class UndoFailureModel(PreflightModel):
    """Inject failure before/after a native Undo state transition.

    Executes production Lisp ownership/cleanup expressions only. This does not
    emulate AutoCAD undo history or prove what caused a reported UNDOCTL=61.
    """
    def __init__(self, runtime, command, after, active=0, flags_after=None):
        super().__init__(runtime)
        self.failure_command = ("_.UNDO", command)
        self.failure_after = after
        self.active_after = active
        self.flags_after = flags_after
        self.injected = False

    def native_command(self, tokens):
        call = tuple(tokens)
        if call == ("_.U",):
            self.calls.append(call)
            self.events.append(call)
            return None
        if call == self.failure_command and not self.injected:
            self.injected = True
            if self.failure_after:
                super().native_command(tokens)
            else:
                self.calls.append(call)
                self.events.append(call)
            self.vars["CMDACTIVE"] = self.active_after
            if self.flags_after is not None:
                self.vars["UNDOCTL"] = self.flags_after
            self.vars["LASTPROMPT"] = "injected Undo native failure"
            raise NativeFailure("injected Undo native failure")
        return super().native_command(tokens)


class CommandProtocolModel(PreflightModel):
    """Consume native command input, not evaluate CAD geometry.

    Visibility's final grip prompt comes from the supplied 08.10 host capture.
    Other routes are bounded contracts from the Autodesk command references.
    The model cannot certify BEDIT, native actions, Undo or DWG persistence.
    """
    def __init__(self, runtime):
        super().__init__(runtime)
        self.vars["BACTIONBARMODE"] = 1
        self.command_error = None
        self.editor_opens_before_error = False

    def native_command(self, tokens):
        self.calls.append(tuple(tokens))
        command, *answers = tokens
        if self.command_error:
            if command == "_.-BEDIT" and self.editor_opens_before_error:
                self.vars["BLOCKEDITOR"] = 1
            self.vars["LASTPROMPT"] = "original native failure"
            raise NativeFailure(self.command_error)
        if command == "_.-BEDIT":
            if len(answers) != 1:
                raise NativeFailure("BEDIT requires one block name")
            self.vars["BLOCKEDITOR"] = 1
        elif command == "_.BPARAMETER":
            kind = answers.pop(0)
            allowed = {"_Name", "_Label", "_Base", "_Palette"}
            while answers and isinstance(answers[0], str) and answers[0] in allowed:
                answers.pop(0)
                if not answers:
                    raise NativeFailure("missing option value")
                answers.pop(0)
            points = 1 if kind == "_Visibility" else 3 if kind == "_Linear" else -1
            if points == -1:
                raise NativeFailure("unmodelled parameter type")
            for _ in range(points):
                if not answers or not isinstance(answers.pop(0), list):
                    raise NativeFailure("missing parameter point")
            maximum = 1 if kind == "_Visibility" else 2
            self.vars["LASTPROMPT"] = f"Введите число ручек [0/{maximum}] <1>:"
            if not answers or type(answers.pop(0)) is not int:
                # command-s does not leave a pending prompt in the main processor.
                self.vars["CMDACTIVE"] = 0
                raise NativeFailure("missing terminal grip count")
            if tokens[-1] not in range(maximum + 1) or answers:
                raise NativeFailure("unexpected parameter answers")
        elif command == "_.BACTIONTOOL":
            kind = answers.pop(0)
            if kind not in {"_Move", "_Stretch"}:
                raise NativeFailure("unmodelled action type")
            required = 6 if kind == "_Stretch" else 4
            if self.vars["BACTIONBARMODE"] == 0:
                required += 1  # An action-location point is required only in mode 0.
            if len(answers) != required or answers[required - (2 if self.vars["BACTIONBARMODE"] == 0 else 1)] != "":
                raise NativeFailure("incomplete action selection/location")
        elif command == "_.BSAVE":
            if answers:
                raise NativeFailure("unexpected BSAVE input")
        elif command == "_.BCLOSE":
            self.vars["BLOCKEDITOR"] = 0
        else:
            raise NativeFailure("unmodelled authoring command: " + command)
        return None


def calls_to(form, name):
    """Read command expressions from source, without a copied command builder."""
    if not isinstance(form, list) or not form:
        return []
    found = [form] if form[0] == name else []
    for child in form[1:]:
        found.extend(calls_to(child, name))
    return found


class DimensionModel(CleanupModel):
    """Evaluate the real pure-Lisp dimension predicate on synthetic snapshots.

    This cannot execute CAD commands, XData propagation or the native evaluator.
    """
    def __init__(self, runtime, data):
        super().__init__(0, False, False, None)
        self.env.update({"afn:data": data, ":vlax-false": 0})
        self.defuns = {f[1]: f for f in parse(runtime) if isinstance(f, list) and f[:1] == ["defun"]}

    def run(self, expression):
        if not isinstance(expression, list) or not expression:
            return super().run(expression)
        head, *args = expression
        if head == "foreach":
            result = None
            for item in self.run(args[1]) or []:
                self.env[args[0]] = item
                for body in args[2:]:
                    result = self.run(body)
            return result
        if head in {"afn:get", "afn:assert", "car", "cdr", "cadr", "cons", "nth", "length", "distance", "equal"}:
            values = [self.run(x) for x in args]
            if head == "afn:get":
                return dict(values[1]).get(values[0])
            if head == "afn:assert":
                if not values[0]:
                    raise NativeFailure(values[1])
                return None
            if head == "car":
                return values[0][0] if values[0] else None
            if head == "cdr":
                if isinstance(values[0], Pair):
                    return values[0][1]
                return values[0][1:] if values[0] else []
            if head == "cadr":
                return values[0][1] if values[0] else None
            if head == "cons":
                return [values[0], *(values[1] or [])]
            if head == "nth":
                return values[1][values[0]]
            if head == "length":
                return len(values[0] or [])
            if head == "distance":
                return math.dist(*values)
            if head == "equal":
                return abs(values[0] - values[1]) <= values[2] if len(values) == 3 and all(isinstance(x, (int, float)) for x in values[:2]) else values[0] == values[1]
        if head in self.defuns:
            definition = self.defuns[head]
            declaration = definition[2]
            divider = declaration.index("/") if "/" in declaration else len(declaration)
            values = [self.run(x) for x in args]
            outer = self.env.copy()
            self.env.update(dict(zip(declaration[:divider], values)))
            self.env.update({x: None for x in declaration[divider + 1:]})
            try:
                result = None
                for body in definition[3:]:
                    result = self.run(body)
                return result
            finally:
                self.env = outer
        return super().run(expression)


class AuthorTests(unittest.TestCase):
    def setUp(self):
        self.data = json.loads((ROOT / "fixtures/rectangles.json").read_text())
        self.runtime = (ROOT / "native_author.lsp").read_text(encoding="utf-8")

    def test_literal_data_cannot_inject_commands(self):
        value = 'КР2 \\ folder "); (command "_.ERASE" "_ALL" "") ;'
        self.assertEqual(parse(lisp(value)), [String(value)])

    def test_valid_fixture_and_reproducible_source(self):
        self.assertIs(validate(self.data), self.data)
        result = render(self.data)
        self.assertEqual(result, render(copy.deepcopy(self.data)))
        forms = parse(result)
        self.assertGreater(len(forms), 20)
        self.assertEqual(forms[0][:2], ["setq", "afn:data"])
        self.assertEqual(audit_scope(forms), [])

    def test_scope_audit_catches_caller_name_corruption(self):
        broken = self.runtime.replace("(reference / properties prop values name)", "(reference / properties prop values)")
        self.assertIn("name", audit_scope(parse(broken)))

    def test_nonfinite_and_boolean_dimensions_rejected(self):
        for value in (float("inf"), float("nan"), True, -1, 0):
            with self.subTest(value=value):
                data = copy.deepcopy(self.data)
                data["defaults"]["AFN_INSULATION"] = value
                with self.assertRaises(ContractError):
                    validate(data)

    def test_ambiguous_sources_and_actions_rejected(self):
        mutations = [
            lambda d: d["entities"].append(copy.deepcopy(d["entities"][0])),
            lambda d: d["actions"][0]["entities"].append("missing"),
            lambda d: d["actions"].append(copy.deepcopy(d["actions"][0])),
            lambda d: d["actions"][0].update(entities=["left"]),
            lambda d: d["actions"][0].update(entities=["dleft"]),
            lambda d: d["entities"][0].update(command="ERASE"),
            lambda d: d.update(block_name="AFN_X|EXTERNAL"),
            lambda d: d.update(provenance={}),
            lambda d: d["parameters"]["AFN_CLADDING_X"].update(start=[10, 100]),
        ]
        for change in mutations:
            data = copy.deepcopy(self.data)
            change(data)
            with self.assertRaises(ContractError):
                validate(data)

    def test_bound_dimension_contract_rejects_move_only_and_ambiguous_links(self):
        mutations = [
            lambda d: d["parameters"]["AFN_INSULATION"].pop("dimension_id"),
            lambda d: d["parameters"]["AFN_INSULATION"].update(dimension_id="left"),
            lambda d: d["parameters"]["AFN_INSULATION"].update(dimension_id="missing"),
            lambda d: d["parameters"]["AFN_CLADDING_X"].update(dimension_id="dleft"),
            lambda d: d["entities"][2].update(variant="RECT_A"),
            lambda d: d["entities"][2].update(p2=[99, 70]),
            lambda d: (d["actions"][0].update(kind="move"), d["actions"][0].pop("frame")),
            lambda d: d["actions"][0].update(frame=[[-1, -1], [101, 85]]),
            lambda d: d["actions"][0].update(frame=[[100, -1], [101, 85]]),
            lambda d: d["actions"][0]["entities"].append("dright"),
        ]
        for change in mutations:
            data = copy.deepcopy(self.data)
            change(data)
            with self.subTest(change=change), self.assertRaises(ContractError):
                validate(data)

    def dimension_snapshot(self):
        def row(ident, end):
            return ["AcDbAlignedDimension", ident, [0, 0, 0], [end, 0, 0], end,
                    [end / 2, 10, 0], "", 1, "", "", 2, 0, 0, 8, 0, "0"]
        return [[row("dleft", 150), row("dright", 280)],
                {"AFN_INSULATION": 150, "AFN_CLADDING_X": 280}, [], []]

    def check_dimensions(self, snapshot):
        model = DimensionModel(self.runtime, self.data)
        model.env["afn:data"] = {**self.data, "parameters": [Pair((k, v)) for k, v in self.data["parameters"].items()]}
        model.env["snapshot-input"] = snapshot
        model.run(["afn:check-dimensions", "snapshot-input"])

    def test_native_dimension_predicate_checks_value_not_translation(self):
        good = self.dimension_snapshot()
        self.check_dimensions(good)
        moved = copy.deepcopy(good)
        moved[0][0][2:6] = [[50, 0, 0], [150, 0, 0], 100, [100, 10, 0]]
        with self.assertRaisesRegex(NativeFailure, "measurement mismatch"):
            self.check_dimensions(moved)
        lying_measurement = copy.deepcopy(moved)
        lying_measurement[0][0][4] = 150
        with self.assertRaisesRegex(NativeFailure, "measurement mismatch"):
            self.check_dimensions(lying_measurement)

    def test_native_dimension_predicate_rejects_wrong_id_and_bad_format(self):
        mutations = [lambda s: s[0].append(copy.deepcopy(s[0][0])),
                     lambda s: s[0][0].__setitem__(1, None)]
        for index, wrong in ((6, "100"), (7, 2), (8, "prefix"), (9, "suffix"),
                             (10, 4), (11, 100), (12, 1), (13, 2), (14, -1), (15, "DIM")):
            mutations.append(lambda s, i=index, v=wrong: s[0][0].__setitem__(i, v))
        for mutate in mutations:
            snapshot = self.dimension_snapshot()
            mutate(snapshot)
            with self.assertRaises(NativeFailure):
                self.check_dimensions(snapshot)

    def handler(self):
        main = next(x for x in parse(self.runtime) if isinstance(x, list) and x[:2] == ["defun", "c:ATFNATIVEBUILD"])
        handler = next(x for x in main[3:] if isinstance(x, list) and x[:2] == ["defun", "*error*"])
        return ["progn", *handler[3:]]

    def preflight(self, message):
        main = next(x for x in parse(self.runtime) if isinstance(x, list) and x[:2] == ["defun", "afn:check-drawing"])
        return next(x for x in main[3:] if isinstance(x, list) and x[:1] == ["afn:assert"] and message in x[-1])

    def test_layer_zero_preflight_rejects_invisible_or_locked_output(self):
        predicate = self.preflight("Слой 0 должен")
        model = PreflightModel()
        model.run(predicate)
        for key, bad in (("LayerOn", 0), ("Freeze", -1), ("Lock", -1)):
            model = PreflightModel()
            model.layer[key] = bad
            with self.assertRaisesRegex(NativeFailure, "Слой 0"):
                model.run(predicate)
            self.assertEqual(model.calls, [])

    def test_reported_view_change_reaches_geometry_with_real_preparation(self):
        # Actual report: UNDOCTL=53, DBMOD=16, DWGTITLED=0, BLOCKEDITOR=0.
        # The old predicate rejected DBMOD=16 before the first drawing change.
        for dbmod in (0, 8, 16, 24):
            model = PreflightModel(self.runtime)
            model.vars["DBMOD"] = dbmod
            model.prepare()
            self.assertEqual(model.calls[0], ("_.UNDO", "_Begin"))
            self.assertEqual(model.events[-1], "geometry reached")
            self.assertEqual(model.env["undo-state"], "open")
            self.assertIn(f"DBMOD={dbmod}", "".join(model.messages))
            self.assertIn("UNDOCTL=53", "".join(model.messages))

    def test_dirty_or_nonempty_drawing_is_rejected_before_dialog_or_undo(self):
        cases = [("DBMOD", x) for x in (*range(1, 8), 9, 17, 25, 32, 40, 48, 64, 128, -1)]
        cases += [("DWGTITLED", 1), ("BLOCKEDITOR", 1), ("TILEMODE", 0), ("CMDACTIVE", 1), ("CMDACTIVE", 2), ("CMDACTIVE", 3)]
        for key, value in cases:
            model = PreflightModel(self.runtime)
            model.vars.update(UNDOCTL=48)
            model.vars[key] = value
            with self.assertRaises(NativeFailure):
                model.prepare()
            self.assertEqual(model.calls, [], (key, value))
            self.assertNotIn("output dialog", model.events)
        for attribute, value in (("count", 1), ("existing_block", "foreign block")):
            model = PreflightModel(self.runtime)
            setattr(model, attribute, value)
            with self.assertRaises(NativeFailure):
                model.prepare()
            self.assertEqual(model.calls, [])
            self.assertNotIn("output dialog", model.events)

    def test_all_undo_flags_are_prepared_without_closing_foreign_groups(self):
        for flags in range(64):
            model = PreflightModel(self.runtime)
            model.vars["UNDOCTL"] = flags
            if flags & 8:
                with self.assertRaisesRegex(NativeFailure, "активна группа UNDO"):
                    model.prepare()
                self.assertEqual(model.calls, [], flags)
                self.assertEqual(model.vars["UNDOCTL"], flags)
                continue
            model.prepare()
            commands = [c for c in model.calls if c[0] == "_.UNDO"]
            normalize = [("_.UNDO", "_All")] if not flags & 1 else [("_.UNDO", "_Control", "_All")] if flags & 2 else []
            self.assertEqual(commands, [*normalize, ("_.UNDO", "_Begin")], flags)
            self.assertEqual(model.vars["UNDOCTL"] & 11, 9)  # All plus our new group.
            if normalize:
                self.assertLess(model.events.index("output dialog"), model.events.index(normalize[0]))
                self.assertIn("Этот режим сохраняется", "".join(model.messages))

    def test_cancel_existing_output_and_state_change_do_not_prepare_undo(self):
        for attribute, value in (("output", None), ("existing_file", "existing.dwg")):
            model = PreflightModel(self.runtime)
            model.vars["UNDOCTL"] = 48
            setattr(model, attribute, value)
            with self.assertRaises(NativeFailure):
                model.prepare()
            self.assertEqual(model.calls, [])
        for key, value in (("DBMOD", 1), ("UNDOCTL", 61), ("DWGTITLED", 1), ("BLOCKEDITOR", 1)):
            model = PreflightModel(self.runtime)
            model.dialog = lambda m, k=key, v=value: m.vars.__setitem__(k, v)
            with self.assertRaises(NativeFailure):
                model.prepare()
            self.assertEqual(model.calls, [], key)

    def test_undo_normalization_readback_and_failure_never_undo_previous_work(self):
        for fail in ("silent refusal", "command failure"):
            model = PreflightModel(self.runtime)
            model.vars["UNDOCTL"] = 48
            model.reject_normalize = fail == "silent refusal"
            model.fail = "normalize" if fail == "command failure" else None
            with self.assertRaises(NativeFailure):
                model.prepare()
            model.run(self.handler())
            self.assertNotIn(("_.UNDO", "_Begin"), model.calls)
            self.assertNotIn(("_.UNDO", "_End"), model.calls)
            self.assertNotIn(("_.U",), model.calls)
            self.assertNotIn(("restore",), model.calls)
            self.assertIsNone(model.env["undo-state"])

    def test_silent_begin_refusal_does_not_claim_ownership_or_reach_geometry(self):
        model = PreflightModel(self.runtime)
        model.reject_begin = True
        with self.assertRaisesRegex(NativeFailure, "не открыл группу"):
            model.prepare()
        model.run(self.handler())
        self.assertNotIn("geometry reached", model.events)
        self.assertNotIn(("_.UNDO", "_End"), model.calls)
        self.assertNotIn(("_.U",), model.calls)
        self.assertNotIn(("restore",), model.calls)
        self.assertIsNone(model.env["undo-state"])

    def test_end_readback_blocks_save_and_undo_when_group_is_still_open(self):
        main = next(x for x in parse(self.runtime) if isinstance(x, list) and x[:2] == ["defun", "c:ATFNATIVEBUILD"])
        ending = self.undo_end_index(main)
        model = PreflightModel(self.runtime)
        model.vars["UNDOCTL"] = 61
        model.env["undo-state"] = "open"
        model.reject_end = True
        with self.assertRaisesRegex(NativeFailure, "не завершил группу"):
            for expression in main[ending:]:
                model.run(expression)
        self.assertEqual(model.env["undo-state"], "open")
        cleanup = CleanupModel(editor=0, owned_editor=False, commands_owned=True, undo_state="open", fail="end-noop")
        cleanup.run(self.handler())
        self.assertNotIn(("_.U",), cleanup.calls)
        self.assertEqual(cleanup.env["undo-state"], "open")

    def undo_end_index(self, main):
        # Locate the main command's End, not the nested error handler. Support
        # historical afn:cmd and the readback-before-reporting implementation
        # so the same regressions replay against the unfixed runtime.
        for index, expression in enumerate(main[3:], 3):
            if not isinstance(expression, list) or expression[:1] == ["defun"]:
                continue
            if any(call[1] == ["quote", ["_.UNDO", "_End"]]
                   for call in calls_to(expression, "afn:cmd")):
                return index
            if any(call[2] == ["quote", ["_.UNDO", "_End"]]
                   for call in calls_to(expression, "vl-catch-all-apply")):
                return index
        raise AssertionError("main Undo End missing")

    def test_begin_failure_readback_closes_only_newly_opened_empty_group(self):
        for after in (False, True):
            model = UndoFailureModel(self.runtime, "_Begin", after)
            with self.assertRaisesRegex(NativeFailure, "injected Undo"):
                model.prepare()
            self.assertEqual(model.env["undo-state"], "open" if after else None)
            self.assertNotIn("geometry reached", model.events)
            self.assertFalse(model.env["settings-changed"])
            model.run(self.handler())
            expected = [("_.UNDO", "_Begin")] + ([("_.UNDO", "_End")] if after else [])
            self.assertEqual(model.calls, expected)
            self.assertEqual(model.vars["UNDOCTL"], 53)
            self.assertNotIn(("_.U",), model.calls)  # An empty group must not consume earlier history.

    def test_end_failure_readback_rolls_back_only_ended_owned_group(self):
        main = next(x for x in parse(self.runtime) if isinstance(x, list) and x[:2] == ["defun", "c:ATFNATIVEBUILD"])
        ending = self.undo_end_index(main)
        for after in (False, True):
            model = UndoFailureModel(self.runtime, "_End", after)
            model.prepare()
            model.owned_definition = True  # Our header existed before the injected End failure.
            with self.assertRaisesRegex(NativeFailure, "injected Undo"):
                for expression in main[ending:]:
                    model.run(expression)
            self.assertEqual(model.env["undo-state"], "ended" if after else "open")
            model.run(self.handler())
            commands = [call for call in model.calls if call[0] in ("_.UNDO", "_.U")]
            expected = [("_.UNDO", "_Begin"), ("_.UNDO", "_End")]
            if not after:
                expected.append(("_.UNDO", "_End"))
            self.assertEqual(commands, expected + [("_.U",)])
            self.assertEqual(model.vars["UNDOCTL"], 53)
            self.assertIn("injected Undo native failure", "".join(model.messages))

    def test_undo_failure_with_active_command_never_cleans_up_or_rolls_back(self):
        main = next(x for x in parse(self.runtime) if isinstance(x, list) and x[:2] == ["defun", "c:ATFNATIVEBUILD"])
        for command in ("_Begin", "_End"):
            for active in (1, 2, 3):
                model = UndoFailureModel(self.runtime, command, after=True, active=active)
                with self.assertRaisesRegex(NativeFailure, "injected Undo"):
                    model.prepare()
                    for expression in main[self.undo_end_index(main):]:
                        model.run(expression)
                before = list(model.calls)
                model.run(self.handler())
                self.assertEqual([call for call in model.calls if call[0] != "restore"], before)
                self.assertNotIn(("_.U",), model.calls)
                self.assertEqual(model.vars["CMDACTIVE"], active)

    def test_cleanup_end_failure_reads_actual_group_state_before_rollback(self):
        for after in (False, True):
            model = UndoFailureModel(self.runtime, "_End", after)
            model.prepare()
            model.owned_definition = True
            model.env["afn:failure"] = "original construction failure"
            model.run(self.handler())
            self.assertEqual(model.env["undo-state"], "ended" if after else "open")
            self.assertEqual(("_.U",) in model.calls, after)
            self.assertIn("original construction failure", "".join(model.messages))

    def test_end_that_loses_all_mode_never_consumes_unknown_undo_history(self):
        main = next(x for x in parse(self.runtime) if isinstance(x, list) and x[:2] == ["defun", "c:ATFNATIVEBUILD"])
        for flags in (0, 2, 3, 48, 50, 51):
            model = UndoFailureModel(self.runtime, "_End", after=True, flags_after=flags)
            model.prepare()
            model.owned_definition = True
            with self.assertRaisesRegex(NativeFailure, "injected Undo"):
                for expression in main[self.undo_end_index(main):]:
                    model.run(expression)
            model.run(self.handler())
            self.assertNotIn(("_.U",), model.calls)

    def test_empty_group_after_first_setting_or_header_failure_never_undoes_history(self):
        class FirstSettingFailure(UndoFailureModel):
            def run(self, expression):
                if isinstance(expression, list) and expression[:1] == ["setvar"]:
                    raise NativeFailure("first setvar failed without mutation")
                return super().run(expression)

        class FirstHeaderFailure(UndoFailureModel):
            def run(self, expression):
                if isinstance(expression, list) and expression[:1] == ["entmake"]:
                    return None  # Native BLOCK creation failed without creating its definition.
                return super().run(expression)

        setting = FirstSettingFailure(self.runtime, "_Never", False)
        with self.assertRaisesRegex(NativeFailure, "first setvar"):
            setting.prepare()
        header = FirstHeaderFailure(self.runtime, "_Never", False)
        header.prepare()
        with self.assertRaisesRegex(NativeFailure, "Cannot create block header"):
            header.run(header.defuns["afn:build-definition"][3])
        for model in (setting, header):
            self.assertTrue(model.env["settings-changed"])
            self.assertFalse(model.owned_definition)
            model.run(self.handler())
            self.assertEqual(model.vars["UNDOCTL"], 53)
            self.assertIn(("_.UNDO", "_End"), model.calls)
            self.assertNotIn(("_.U",), model.calls)
            self.assertIn(("restore",), model.calls)

    def test_own_undo_setting_change_is_not_rejected_as_input_dbmod(self):
        model = PreflightModel(self.runtime)
        model.vars.update(UNDOCTL=48, DBMOD=16)
        # CAD double injects an additional dirty bit after our own command.
        # This tests ordering, not a claim that native UNDO sets DBMOD=4.
        model.normalize_dbmod = 20
        model.prepare()
        self.assertEqual(model.events[-1], "geometry reached")
        self.assertIn("DBMOD=20", "".join(model.messages))

    def test_preflight_failure_does_not_discard_foreign_editor_or_command(self):
        model = CleanupModel(editor=1, owned_editor=False, commands_owned=False, undo_state=None, active=1)
        model.run(self.handler())
        self.assertEqual(model.calls, [])
        self.assertEqual(model.vars["BLOCKEDITOR"], 1)
        self.assertEqual(model.vars["CMDACTIVE"], 1)
        self.assertIn("\nAFN STAGE: test stage", model.messages)
        self.assertIn("\nAFN LAST PROMPT: failed native prompt", model.messages)

    def command_expressions(self, command):
        definition = next(f for f in parse(self.runtime) if isinstance(f, list) and f[:2] == ["defun", "afn:build-definition"])
        return [x for x in calls_to(definition, "afn:cmd")
                if isinstance(x[1], list) and x[1][:2] == ["list", command]]

    def test_visibility_consumes_captured_final_grip_prompt(self):
        expression = self.command_expressions("_.BPARAMETER")[0]
        model = CommandProtocolModel(self.runtime)
        model.run(expression)
        self.assertEqual(model.calls[-1][-1], 1)
        # This is the exact pre-08.10 omission, not an invented host failure.
        broken = copy.deepcopy(expression)
        broken[1].pop()
        with self.assertRaisesRegex(NativeFailure, "missing terminal grip"):
            model.run(broken)
        self.assertEqual(model.vars["CMDACTIVE"], 0)
        self.assertEqual(model.env["afn:failed-prompt"], "Введите число ручек [0/1] <1>:")

    def test_linear_and_action_streams_consume_complete_prompt_contracts(self):
        model = CommandProtocolModel(self.runtime)
        spec = self.data["parameters"]["AFN_INSULATION"]
        model.env.update({"name-p": "AFN_INSULATION", "spec": spec, "ename": "parameter-entity",
                          "frame": [[0, 0], [100, 100]], "selected": ["geometry-1"]})
        model.run(self.command_expressions("_.BPARAMETER")[1])
        for expression in self.command_expressions("_.BACTIONTOOL"):
            model.run(expression)
            broken = copy.deepcopy(expression)
            broken[1].pop()  # A missing end-of-selection must not pass.
            with self.assertRaisesRegex(NativeFailure, "incomplete action"):
                model.run(broken)
        # Read the actual settings path, rather than assuming action-bar mode.
        prepared = PreflightModel(self.runtime)
        prepared.vars["BACTIONBARMODE"] = 0
        prepared.prepare()
        self.assertEqual(prepared.vars["BACTIONBARMODE"], 1)
        model.vars["BACTIONBARMODE"] = 0
        with self.assertRaisesRegex(NativeFailure, "incomplete action"):
            model.run(self.command_expressions("_.BACTIONTOOL")[0])

    def test_error_context_disallows_command_and_early_rejection_is_not_geometry_failure(self):
        model = CleanupModel(editor=0, owned_editor=False, commands_owned=False, undo_state=None)
        with self.assertRaisesRegex(NativeFailure, "command is forbidden"):
            model.run(["command"])
        self.assertEqual(calls_to(self.handler(), "command"), [])
        self.assertNotIn("*push-error-using-command*", self.runtime)
        model.run(self.handler())
        text = "".join(model.messages)
        self.assertIn("Построение узла не начато", text)
        self.assertNotIn("закройте этот новый чертёж", text)

    def test_open_editor_error_records_ownership_before_reporting(self):
        for opened in (False, True):
            model = CommandProtocolModel(self.runtime)
            model.command_error = "BEDIT failed"
            model.editor_opens_before_error = opened
            with self.assertRaisesRegex(NativeFailure, "BEDIT failed"):
                model.run(["afn:open-editor", String("AFN_TEST")])
            self.assertEqual(bool(model.env["afn:editor-owned"]), opened)
            self.assertEqual(model.env["afn:failed-prompt"], "original native failure")
            self.assertEqual(model.env["afn:last-command"], "_.-BEDIT")

    def test_unknown_active_prompt_prevents_close_end_and_undo(self):
        for active in (1, 2, 3):
            model = CleanupModel(editor=1, owned_editor=True, commands_owned=True,
                                 undo_state="open", active=active)
            model.run(self.handler())
            self.assertEqual(model.calls, [("restore",)])
            self.assertIn("автоматический откат не выполнялся", "".join(model.messages))
            self.assertEqual(model.vars["BLOCKEDITOR"], 1)
            self.assertEqual(model.vars["UNDOCTL"], 61)

    def test_original_diagnostic_survives_cleanup_failure_and_prompt_changes(self):
        for failure in ("_.BCLOSE", "_.UNDO", "restore", "close-noop", "end-noop"):
            model = CleanupModel(editor=1, owned_editor=True, commands_owned=True,
                                 undo_state="open", fail=failure)
            model.env["afn:failed-prompt"] = "captured grip prompt"
            model.env["afn:failure"] = "original failure"
            model.run(self.handler())
            self.assertIn("\nAFN BUILD ABORTED: original failure", model.messages)
            self.assertIn("\nAFN LAST PROMPT: captured grip prompt", model.messages)
            if failure in ("_.BCLOSE", "_.UNDO", "close-noop", "end-noop"):
                self.assertNotIn(("_.U",), model.calls)

    def test_own_editor_failure_closes_before_rollback(self):
        model = CleanupModel(editor=1, owned_editor=True, commands_owned=True, undo_state="open")
        model.run(self.handler())
        self.assertEqual(model.calls, [("_.BCLOSE", "_Discard"), ("_.UNDO", "_End"), ("_.U",), ("restore",)])
        self.assertIn("\nAFN LAST PROMPT: failed native prompt", model.messages)

    def test_save_failure_after_group_end_rolls_back_own_group(self):
        model = CleanupModel(editor=0, owned_editor=False, commands_owned=True, undo_state="ended")
        model.run(self.handler())
        self.assertEqual(model.calls, [("_.U",), ("restore",)])

    def test_failed_close_or_end_never_undoes_unknown_previous_work(self):
        model = CleanupModel(editor=1, owned_editor=True, commands_owned=True, undo_state="open", fail="_.BCLOSE")
        model.run(self.handler())
        self.assertNotIn(("_.U",), model.calls)
        model = CleanupModel(editor=0, owned_editor=False, commands_owned=True, undo_state="open", fail="_.UNDO")
        model.run(self.handler())
        self.assertNotIn(("_.U",), model.calls)


if __name__ == "__main__":
    unittest.main(verbosity=2)
