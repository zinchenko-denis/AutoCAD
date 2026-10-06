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
            while cursor < len(tokens) and tokens[cursor] != ")":
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
    globals_allowed = {"afn:data", "afn:failure", "afn:editor-owned", "afn:stage", "afn:last-command"}
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
    def __init__(self, editor, owned_editor, commands_owned, undo_state, fail=None):
        self.vars = {"BLOCKEDITOR": editor, "CMDACTIVE": 1, "LASTPROMPT": "failed native prompt"}
        self.env = {"afn:editor-owned": owned_editor, "commands-owned": commands_owned,
                    "undo-state": undo_state, "settings": [], "afn:failure": None, "message": "test",
                    "afn:stage": "test stage", "afn:last-command": "_.BPARAMETER"}
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
        if head == "getvar":
            return self.vars[values[0]]
        if head == "strcat":
            return "".join(values)
        if head == "princ":
            self.messages.extend(values)
            return None
        if head == "command":
            self.calls.append(("cancel",))
            self.vars["CMDACTIVE"] = 0
            return None
        if head == "afn:restore":
            self.calls.append(("restore",))
            return None
        if head == "vl-catch-all-error-p":
            return isinstance(values[0], NativeFailure)
        if head == "vl-catch-all-apply":
            fn, tokens = values
            if fn != "command-s":
                raise AssertionError(fn)
            call = tuple(str(x) for x in tokens)
            self.calls.append(call)
            self.vars["LASTPROMPT"] = "cleanup prompt: " + call[0]
            if self.fail == call[0]:
                return NativeFailure(call[0])
            if call[0] == "_.BCLOSE":
                self.vars["BLOCKEDITOR"] = 0
            return None
        raise AssertionError(f"unmodelled error-handler expression: {head}")


class PreflightModel(CleanupModel):
    def __init__(self):
        super().__init__(0, False, False, None)
        self.env.update({":vlax-true": -1, ":vlax-false": 0, "layer-zero": object()})
        self.layer = {"LayerOn": -1, "Freeze": 0, "Lock": 0}

    def run(self, expression):
        if isinstance(expression, list) and expression:
            if expression[0] == "afn:assert":
                if not self.run(expression[1]):
                    raise NativeFailure(self.run(expression[2]))
                return None
            if expression[0] in ("vla-get-LayerOn", "vla-get-Freeze", "vla-get-Lock"):
                return self.layer[expression[0][8:]]
        return super().run(expression)


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
        main = next(x for x in parse(self.runtime) if isinstance(x, list) and x[:2] == ["defun", "c:ATFNATIVEBUILD"])
        return next(x for x in main[3:] if isinstance(x, list) and x[:1] == ["afn:assert"] and message in x[-1])

    def test_layer_zero_preflight_rejects_invisible_or_locked_output(self):
        predicate = self.preflight("Layer 0 must")
        model = PreflightModel()
        model.run(predicate)
        for key, bad in (("LayerOn", 0), ("Freeze", -1), ("Lock", -1)):
            model = PreflightModel()
            model.layer[key] = bad
            with self.assertRaisesRegex(NativeFailure, "Layer 0"):
                model.run(predicate)
            self.assertEqual(model.calls, [])

    def test_undo_preflight_rejects_disabled_single_or_foreign_group(self):
        predicate = self.preflight("UNDO must")
        for flags in (1, 5, 53):
            model = PreflightModel()
            model.vars["UNDOCTL"] = flags
            model.run(predicate)
        for flags in (0, 2, 3, 9, 61):
            model = PreflightModel()
            model.vars["UNDOCTL"] = flags
            with self.assertRaisesRegex(NativeFailure, "UNDO must"):
                model.run(predicate)
            self.assertEqual(model.calls, [])

    def test_preflight_failure_does_not_discard_foreign_editor_or_command(self):
        model = CleanupModel(editor=1, owned_editor=False, commands_owned=False, undo_state=None)
        model.run(self.handler())
        self.assertEqual(model.calls, [])
        self.assertEqual(model.vars["BLOCKEDITOR"], 1)
        self.assertEqual(model.vars["CMDACTIVE"], 1)
        self.assertIn("\nAFN STAGE: test stage", model.messages)
        self.assertIn("\nAFN LAST PROMPT: failed native prompt", model.messages)

    def test_own_editor_failure_closes_before_rollback(self):
        model = CleanupModel(editor=1, owned_editor=True, commands_owned=True, undo_state="open")
        model.run(self.handler())
        self.assertEqual(model.calls, [("cancel",), ("_.BCLOSE", "_Discard"), ("_.UNDO", "_End"), ("_.U",), ("restore",)])
        self.assertIn("\nAFN LAST PROMPT: failed native prompt", model.messages)

    def test_save_failure_after_group_end_rolls_back_own_group(self):
        model = CleanupModel(editor=0, owned_editor=False, commands_owned=True, undo_state="ended")
        model.run(self.handler())
        self.assertEqual(model.calls, [("cancel",), ("_.U",), ("restore",)])

    def test_failed_close_or_end_never_undoes_unknown_previous_work(self):
        model = CleanupModel(editor=1, owned_editor=True, commands_owned=True, undo_state="open", fail="_.BCLOSE")
        model.run(self.handler())
        self.assertNotIn(("_.U",), model.calls)
        model = CleanupModel(editor=0, owned_editor=False, commands_owned=True, undo_state="open", fail="_.UNDO")
        model.run(self.handler())
        self.assertNotIn(("_.U",), model.calls)


if __name__ == "__main__":
    unittest.main(verbosity=2)
