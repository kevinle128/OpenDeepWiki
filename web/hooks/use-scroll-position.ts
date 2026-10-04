'use client';

import * as React from 'react';

interface ScrollPosition {
  y: number;
  isScrolled: boolean;
}

const DEFAULT_THRESHOLD = 100;

/**
 * Custom hook to track scroll position with throttling for performance optimization.
 * @param threshold - The scroll position threshold to determine if the page is scrolled (default: 100)
 * @returns ScrollPosition object containing current y position and isScrolled boolean
 */
export function useScrollPosition(threshold: number = DEFAULT_THRESHOLD): ScrollPosition {
  const [scrollPosition, setScrollPosition] = React.useState<ScrollPosition>({
    y: 0,
    isScrolled: false,
  });

  React.useEffect(() => {
    let ticking = false;
    let frameId = 0;

    const handleScroll = () => {
      if (!ticking) {
        frameId = window.requestAnimationFrame(() => {
          const y = window.scrollY;
          setScrollPosition({
            y,
            isScrolled: y > threshold,
          });
          ticking = false;
        });
        ticking = true;
      }
    };

    // Set initial position
    handleScroll();

    window.addEventListener('scroll', handleScroll, { passive: true });

    return () => {
      window.removeEventListener('scroll', handleScroll);
      window.cancelAnimationFrame(frameId);
    };
  }, [threshold]);

  return { y: scrollPosition.y, isScrolled: scrollPosition.y > threshold };
}
