import { Breadcrumb } from '@/components/az/Chrome';
import { CommandBar } from '@/components/az/CommandBar';
import { Essentials } from '@/components/az/Essentials';
import { MessageBar, PageHead } from '@/components/az/Surfaces';
import { LiveConsole } from '@/components/az/LiveConsole';
import { IconPhone } from '@/components/az/Icons';
import { getRuntimeConfig } from '@/lib/azure/mediaService';

export const dynamic = 'force-dynamic';
export const revalidate = 0;

export default async function LivePage() {
  const config = await getRuntimeConfig();

  // The browser connects to the media service's SignalR hub directly, so this has to be
  // the public FQDN rather than an internal address.
  const hubUrl = `${(process.env.MEDIA_SERVICE_URL ?? '').replace(/\/$/, '')}/hubs/live`;

  return (
    <>
      <Breadcrumb trail={['EntraGuard', 'Live calls']} />
      <PageHead
        title="Live calls"
        subtitle="Transcript, risk trajectory, and every decision the system makes — streamed while the call is still up."
        icon={<IconPhone size={17} />}
      />
      {/* No time range: this page is a live stream, not a query over a window. */}
      <CommandBar timeRange={false} />

      <div className="az-content">
        <Essentials
          items={[
            { label: 'Media service', value: <span className="mono">{process.env.MEDIA_SERVICE_URL ?? '—'}</span> },
            { label: 'Analyst model', value: <span className="mono">{config.data?.model ?? '—'}</span> },
            { label: 'Scoring cadence', value: config.data ? `${config.data.analysisIntervalMs / 1000}s` : '—' },
            { label: 'Context window', value: config.data ? `${config.data.analysisWindowMs / 1000}s` : '—' },
            { label: 'Remediation tier', value: !config.data
                ? <span className="az-badge">Unknown</span>
                : config.data.riskTier === 'Graph'
                  ? <span className="az-badge success">Full</span>
                  : <span className="az-badge warning">Degraded</span> },
            { label: 'Autonomous actions', value: !config.data
                ? <span className="az-badge">Unknown</span>
                : config.data.autonomousActionsEnabled
                  ? <span className="az-badge success">Enabled</span>
                  : <span className="az-badge warning">Shadow mode</span> },
          ]}
        />

        {config.degraded && (
          <MessageBar intent="error" title="Media service unreachable.">{config.degraded}</MessageBar>
        )}

        <LiveConsole hubUrl={hubUrl} />
      </div>
    </>
  );
}
