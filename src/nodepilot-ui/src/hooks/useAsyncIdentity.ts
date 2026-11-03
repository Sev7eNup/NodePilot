import { useCallback, useLayoutEffect, useRef } from 'react';

/** Captures ownership of an editing session across awaits, including A → B → A navigation. */
export function useAsyncIdentity(identity: unknown) {
  const owner = useRef({ generation: 0 });
  useLayoutEffect(() => {
    const session = owner.current;
    session.generation++;
    return () => { session.generation++; };
  }, [identity]);
  return useCallback(() => {
    const generation = owner.current.generation;
    return () => owner.current.generation === generation;
  }, []);
}
