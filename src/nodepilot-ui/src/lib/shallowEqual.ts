/**
 * True when both objects have the same keys and each value is `Object.is`-equal.
 *
 * Written for React.memo comparators on canvas elements. The canvas projection rebuilds a
 * node's or edge's `data` object on every pass, so reference equality on it always reports a
 * change even when nothing about the element differs. Comparing the entries instead keeps the
 * comparator exact — no key list to maintain, and any real value change still re-renders.
 */
export function shallowEqual(
  a: Record<string, unknown> | undefined,
  b: Record<string, unknown> | undefined,
): boolean {
  if (a === b) return true;
  if (!a || !b) return false;

  const aKeys = Object.keys(a);
  if (aKeys.length !== Object.keys(b).length) return false;
  for (const key of aKeys) {
    if (!Object.is(a[key], b[key])) return false;
  }
  return true;
}
