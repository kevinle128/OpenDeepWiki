import { act, renderHook } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { useControlledState } from '../use-controlled-state';

describe('useControlledState', () => {
  it('keeps controlled values authoritative and retains them when control ends', () => {
    const onChange = vi.fn();
    const { result, rerender } = renderHook(
      ({ value }: { value: string | undefined }) =>
        useControlledState({ value, defaultValue: 'default', onChange }),
      { initialProps: { value: 'first' as string | undefined } },
    );

    act(() => result.current[1]('requested'));
    expect(onChange).toHaveBeenCalledWith('requested');
    expect(result.current[0]).toBe('first');

    rerender({ value: 'second' });
    expect(result.current[0]).toBe('second');
    rerender({ value: undefined });
    expect(result.current[0]).toBe('second');
    act(() => result.current[1]('local'));
    expect(result.current[0]).toBe('local');
  });
});
