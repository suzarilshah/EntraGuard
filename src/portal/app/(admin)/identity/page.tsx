import Link from 'next/link';
import { Breadcrumb } from '@/components/az/Chrome';
import { CommandBar } from '@/components/az/CommandBar';
import { Essentials } from '@/components/az/Essentials';
import { Card, MessageBar, PageHead } from '@/components/az/Surfaces';
import { DataTable } from '@/components/az/DataTable';
import { AdminGlyph } from '@/components/az/AdminGlyph';
import { getDirectoryCapabilities, getDirectoryUsers, getRecentSignIns, getRiskyUsers, getTenant } from '@/lib/azure/graph';
import { timeRange } from '@/lib/adminData';

export const dynamic = 'force-dynamic';
export default async function IdentityPage({searchParams}:{searchParams:Promise<{tab?:string;q?:string;hours?:string}>}) {
  const params=await searchParams; const tab=['users','signins','risk'].includes(params.tab??'')?params.tab!:'users';
  const hours=timeRange(params.hours); const q=(params.q??'').trim().slice(0,80);
  const [tenant,capabilities,users]=await Promise.all([getTenant(),getDirectoryCapabilities(),tab==='users'?getDirectoryUsers(q):Promise.resolve(null)]);
  const signIns=tab==='signins'&&capabilities.data?.p1!==false?await getRecentSignIns(50,hours):null;
  const risk=tab==='risk'&&capabilities.data?.p2!==false?await getRiskyUsers():null;
  return <><Breadcrumb trail={['EntraGuard','Microsoft Entra ID']} /><PageHead title="Microsoft Entra ID" subtitle="Read-only identity context from the hosting tenant. Verification subjects in other tenants are not automatically included in these Graph lists." icon={<AdminGlyph name="identity" size={27}/>} />
    <CommandBar timeRange={tab==='signins'}><a className="az-cmd" href="https://entra.microsoft.com" target="_blank" rel="noreferrer">Open Entra admin center <AdminGlyph name="external" size={13}/></a></CommandBar>
    <nav className="admin-tabs" aria-label="Identity views">{[{id:'users',label:'Directory users'},{id:'signins',label:'Sign-in logs'},{id:'risk',label:'Identity Protection'}].map(item=><Link prefetch={false} key={item.id} href={`/identity?tab=${item.id}&hours=${hours}`} aria-current={tab===item.id?'page':undefined}>{item.label}</Link>)}</nav>
    <div className="az-content"><Essentials items={[
      {label:'Directory',value:tenant.data?.displayName??'Unavailable'}, {label:'Tenant ID',value:tenant.data?.id??'Unavailable'},
      {label:'Default domain',value:tenant.data?.verifiedDomains.find(d=>d.isDefault)?.name??'Not returned'},
      {label:'Premium subscription',value:!capabilities.data?'Unknown':capabilities.data.p2?'P2 service plan reported':capabilities.data.p1?'P1 service plan reported':'P1/P2 not reported'},
    ]}/>
      {tenant.degraded&&<MessageBar intent="warning">{tenant.degraded}</MessageBar>}
      {capabilities.degraded&&<MessageBar intent="warning" title="Subscription visibility limited.">{capabilities.degraded} API permission and user entitlement are separate from subscription discovery.</MessageBar>}
      {tab==='users'&&users&&<>
        <Card title="Find directory users" source="graph"><form className="admin-search-form" action="/identity" method="get"><input type="hidden" name="tab" value="users"/><input type="search" name="q" defaultValue={q} maxLength={80} aria-label="Directory name or UPN prefix" placeholder="Name or user principal name starts with…"/><button className="az-cmd admin-primary" type="submit">Search directory</button>{q&&<Link prefetch={false} href="/identity?tab=users">Clear search</Link>}</form></Card>
        <Card title="Directory users" source="graph" degraded={users.degraded} flush footer="Up to 50 users are loaded. Search sends a name/UPN-prefix query to Graph; the table filter searches only this loaded result set. Account enabled status is not MFA enrollment status."><DataTable title="Directory users" columns={[{key:'Name'},{key:'User principal name'},{key:'Type'},{key:'Account enabled',format:'boolean'},{key:'Object ID'}]} rows={users.data.map(user=>[user.displayName,user.userPrincipalName,user.userType,user.accountEnabled,user.id])} emptyTitle="No users matched" emptyDetail="Try another name or user-principal-name prefix." /></Card>
      </>}
      {tab==='signins'&&(capabilities.data?.p1===false?<Card title="Sign-in logs require premium capability" source="graph"><MessageBar intent="warning" title="P1/P2 not reported in this tenant.">The managed identity may have AuditLog.Read.All, but sign-in log retrieval also requires the relevant tenant licence. No sign-in totals or empty success state are inferred.</MessageBar><p className="admin-meta-note">Use the Entra admin center to review licensing and permissions. This console does not grant consent or change licences.</p></Card>:signIns&&<Card title={`Recent sign-ins · last ${hours}h`} source="graph" degraded={signIns.degraded} flush footer="Latest 50 sign-ins in the window; not a total. Risk values can be hidden without P2. No applied Conditional Access policy details are requested."><DataTable title="Sign-in logs" columns={[{key:'When',format:'datetime'},{key:'User'},{key:'Application'},{key:'Result',format:'signInResult'},{key:'Risk',format:'riskLevel'},{key:'IP address'},{key:'Location'},{key:'Device'},{key:'Failure reason'}]} rows={signIns.data.map(s=>[s.createdDateTime,s.userPrincipalName,s.appDisplayName,s.status?.errorCode,s.riskLevelDuringSignIn,s.ipAddress,[s.location?.city,s.location?.countryOrRegion].filter(Boolean).join(', '),[s.deviceDetail?.operatingSystem,s.deviceDetail?.browser].filter(Boolean).join(' · '),s.status?.failureReason])} emptyTitle="No sign-ins returned in this window" emptyDetail="This is the Graph response for the current hosting tenant and time range." /></Card>)}
      {tab==='risk'&&(capabilities.data?.p2===false?<Card title="Identity Protection requires P2" source="graph"><MessageBar intent="warning" title="P2 not reported in this tenant.">Risky-user data cannot be assumed available. This is an availability limit, not evidence of zero risky users.</MessageBar><p className="admin-meta-note">An appropriate IdentityRiskyUser permission and licensing are required. No dismiss/confirm-compromised controls are exposed in this read-only blade.</p></Card>:risk&&<Card title="Risky users" source="graph" degraded={risk.degraded} flush footer="Up to 50 returned records. A risk state is Microsoft Entra evidence, separate from EntraGuard's voice analysis."><DataTable title="Risky users" columns={[{key:'Name'},{key:'UPN'},{key:'Risk level',format:'riskLevel'},{key:'Risk state'},{key:'Detail'},{key:'Updated',format:'datetime'}]} rows={risk.data.map(user=>[user.userDisplayName,user.userPrincipalName,user.riskLevel,user.riskState,user.riskDetail,user.riskLastUpdatedDateTime])} emptyTitle="No risky users returned" emptyDetail="The API returned an empty set for the hosting tenant." /></Card>)}
    </div></>;
}
