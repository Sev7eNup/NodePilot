import { lazy, Suspense } from 'react';
import type { ComponentProps } from 'react';
import type { EChart as EChartComponent } from './EChart';

const EChartImpl = lazy(() => import('./EChart').then((m) => ({ default: m.EChart })));

type EChartProps = ComponentProps<typeof EChartComponent>;

/**
 * The charting library, loaded only by the two pages that draw charts.
 *
 * ECharts is a large dependency, and the dashboard is one of the few eagerly imported routes —
 * importing the chart directly put the whole library into the boot chunk, so every page paid for
 * it, including the login screen. The chunk is fetched while the page's own data is still in
 * flight, so nothing waits on it that was not already waiting.
 *
 * The placeholder keeps the chart's box, so the card does not resize when the chart arrives.
 */
export function EChart(props: Readonly<EChartProps>) {
  const { className, style, ariaLabel } = props;
  return (
    <Suspense fallback={<div className={className} style={style} role="img" aria-label={ariaLabel} />}>
      <EChartImpl {...props} />
    </Suspense>
  );
}
