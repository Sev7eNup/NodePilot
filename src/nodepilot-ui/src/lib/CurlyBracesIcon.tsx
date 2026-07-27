import { forwardRef } from 'react';
import { Icon, type CarbonIconProps, type CarbonIconType } from '@carbon/icons-react';

/**
 * JSON object braces in Carbon's standard 32 × 32 icon container.
 * Carbon does not provide a standalone curly-braces glyph.
 */
export const CurlyBracesIcon: CarbonIconType = forwardRef<SVGSVGElement, CarbonIconProps>(
  function CurlyBracesIcon({ children, size = 16, ...rest }, ref) {
    return (
      <Icon
        width={size}
        height={size}
        ref={ref}
        xmlns="http://www.w3.org/2000/svg"
        viewBox="0 0 32 32"
        fill="none"
        stroke="currentColor"
        strokeWidth="2.4"
        strokeLinecap="square"
        strokeLinejoin="miter"
        {...rest}
      >
        <path d="M12 5c-3 0-4 2-4 5v3c0 2-1 3-3 3 2 0 3 1 3 3v3c0 3 1 5 4 5" />
        <path d="M20 5c3 0 4 2 4 5v3c0 2 1 3 3 3-2 0-3 1-3 3v3c0 3-1 5-4 5" />
        {children}
      </Icon>
    );
  },
);
