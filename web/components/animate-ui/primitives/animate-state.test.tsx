import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { Highlight, useHighlight } from './effects/highlight';
import { Accordion, AccordionItem, useAccordionItem } from './radix/accordion';

afterEach(cleanup);

function HighlightState() {
  const { activeValue, setActiveValue } = useHighlight();
  return <button onClick={() => setActiveValue('second')}>{activeValue ?? 'none'}</button>;
}

function AccordionState() {
  const { isOpen } = useAccordionItem();
  return <output>{isOpen ? 'open' : 'closed'}</output>;
}

describe('animation state', () => {
  it('keeps a controlled highlight bound to its value and applies new values', () => {
    const { rerender } = render(
      <Highlight controlledItems value="first"><HighlightState /></Highlight>,
    );
    fireEvent.click(screen.getByRole('button'));
    expect(screen.getByRole('button')).toHaveTextContent('first');
    rerender(<Highlight controlledItems value="second"><HighlightState /></Highlight>);
    expect(screen.getByRole('button')).toHaveTextContent('second');
  });

  it('uses a default highlight only for initial state', () => {
    const { rerender } = render(
      <Highlight controlledItems defaultValue="first"><HighlightState /></Highlight>,
    );
    fireEvent.click(screen.getByRole('button'));
    rerender(<Highlight controlledItems defaultValue="first"><HighlightState /></Highlight>);
    expect(screen.getByRole('button')).toHaveTextContent('second');
  });

  it('matches a single accordion item by its full value', () => {
    const { rerender } = render(
      <Accordion type="single" value="item-long">
        <AccordionItem value="item"><AccordionState /></AccordionItem>
      </Accordion>,
    );
    expect(screen.getByRole('status')).toHaveTextContent('closed');
    rerender(
      <Accordion type="single" value="item">
        <AccordionItem value="item"><AccordionState /></AccordionItem>
      </Accordion>,
    );
    expect(screen.getByRole('status')).toHaveTextContent('open');
  });
});
