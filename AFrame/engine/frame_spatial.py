# -*- coding: utf-8 -*-
"""Operation-local broad-phase index; callers retain their exact predicates.

Two presorted coordinate lists are partitioned at each level, so construction
does not repeatedly sort subtrees. The index stores source indices, never a
rounded coordinate identity. Nonfinite records/queries use a conservative
fallback and remain subject to the caller's original comparisons.
"""
import math


class BoundsIndex:
    """Balanced bounding boxes for records (index, xmin, xmax, ymin, ymax)."""

    LEAF_SIZE = 12

    def __init__(self, records, diagnostics=None):
        self._diagnostics = diagnostics
        records = list(records)
        self._all = [r[0] for r in records]
        self._unindexed = [r[0] for r in records
                           if not all(math.isfinite(v) for v in r[1:])]
        finite = [r for r in records if all(math.isfinite(v) for v in r[1:])]
        xs = sorted(finite, key=lambda r: (r[1] / 2.0 + r[2] / 2.0, r[0]))
        ys = sorted(finite, key=lambda r: (r[3] / 2.0 + r[4] / 2.0, r[0]))
        self._root = self._build(xs, ys)

    @classmethod
    def _build(cls, xs, ys):
        if not xs:
            return None
        bounds = (min(r[1] for r in xs), max(r[2] for r in xs),
                  min(r[3] for r in xs), max(r[4] for r in xs))
        if len(xs) <= cls.LEAF_SIZE:
            return bounds, None, None, xs
        xspread = (xs[-1][1] / 2.0 + xs[-1][2] / 2.0) - \
                  (xs[0][1] / 2.0 + xs[0][2] / 2.0)
        yspread = (ys[-1][3] / 2.0 + ys[-1][4] / 2.0) - \
                  (ys[0][3] / 2.0 + ys[0][4] / 2.0)
        ordered = xs if xspread >= yspread else ys
        left_ids = {r[0] for r in ordered[:len(ordered) // 2]}
        left_x, right_x, left_y, right_y = [], [], [], []
        for record in xs:
            (left_x if record[0] in left_ids else right_x).append(record)
        for record in ys:
            (left_y if record[0] in left_ids else right_y).append(record)
        return (bounds, cls._build(left_x, left_y),
                cls._build(right_x, right_y), None)

    @staticmethod
    def _axis_overlap(low, high, interval, near):
        if near is not None:
            coordinate, tolerance = near
            # Do not replace abs(point-coordinate)<=tol with coordinate +/- tol:
            # floating subtraction at the boundary can round differently.
            if coordinate < low:
                return abs(low - coordinate) <= tolerance
            if coordinate > high:
                return abs(high - coordinate) <= tolerance
            return True
        return not (high < interval[0] or low > interval[1])

    def query(self, x=None, y=None, near_x=None, near_y=None):
        """Return candidates in original source order, without merging any."""
        values = (near_x if near_x is not None else x) + \
                 (near_y if near_y is not None else y)
        if not all(math.isfinite(v) for v in values):
            return sorted(self._all)
        if (x is not None and x[0] > x[1]) or (y is not None and y[0] > y[1]):
            return []
        found = list(self._unindexed)
        pending = [self._root] if self._root is not None else []
        while pending:
            bounds, left, right, records = pending.pop()
            if self._diagnostics is not None:
                self._diagnostics['index_nodes_visited'] += 1
            if not self._axis_overlap(bounds[0], bounds[1], x, near_x) or \
                    not self._axis_overlap(bounds[2], bounds[3], y, near_y):
                continue
            if records is not None:
                for record in records:
                    if self._axis_overlap(record[1], record[2], x, near_x) and \
                            self._axis_overlap(record[3], record[4], y, near_y):
                        found.append(record[0])
            else:
                pending.extend((right, left))
        found.sort()
        return found
