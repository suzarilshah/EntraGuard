import test from 'node:test';
import assert from 'node:assert/strict';
import { timeRange, verificationBase, overviewQueries, odataPrefix, queryNumber } from '../lib/adminData.ts';
import { ADMIN_NAV, isAdminPath } from '../lib/adminNavigation.ts';
import { tableCsv, safeControlPlaneLink, fullCell } from '../lib/adminTable.ts';

test('only supported time windows reach KQL', () => {
  for (const value of [undefined, '', '-1', 'NaN', 'Infinity', '24; union *', '99999999']) assert.equal(timeRange(value), 24);
  for (const value of [1,6,24,168,720]) assert.equal(timeRange(String(value)), value);
  assert.match(verificationBase(Number.NaN), /ago\(24h\)/);
});
test('summary deduplicates retries and does not count a capped sample or pretend grants are attacks', () => {
  const query = overviewQueries(24).summary;
  assert.match(query, /arg_max\(TimeGenerated, \*\) by VerificationId/);
  assert.doesNotMatch(query, /\btake\b|GrantsAccess/);
  assert.match(query, /StepUpRequired/);
  assert.match(query, /NotCompleted=countif/);
});
test('simulation scope is explicit and cannot alter the query structure', () => {
  assert.match(verificationBase(24), /ApplicationName !contains "\(simulated\)"/);
  assert.doesNotMatch(verificationBase(24, true), /ApplicationName !contains/);
});
test('every navigation blade and its nested paths use the operator guard', () => {
  for (const item of ADMIN_NAV) {
    assert.equal(isAdminPath(item.href), true);
    if (item.href !== '/') { assert.equal(isAdminPath(`${item.href}/`), true); assert.equal(isAdminPath(`${item.href}/detail`), true); }
  }
  for (const path of ['/app','/settings','/phone','/handbook','/operator-signin','/identity-other']) assert.equal(isAdminPath(path), false);
});
test('Graph prefixes escape apostrophes and are length-bounded', () => {
  assert.equal(odataPrefix(" O'Connor "), "O''Connor");
  assert.equal(odataPrefix('a'.repeat(100)).length, 80);
  assert.equal(odataPrefix("x') or accountEnabled eq false or ('"), "x'') or accountEnabled eq false or (''");
});
test('unavailable telemetry cannot become a reassuring zero', () => {
  assert.equal(queryNumber({data:{columns:['Total'],rows:[[0]]}},'Total'),0);
  assert.equal(queryNumber({data:{columns:['Total'],rows:[[0]]},degraded:'Forbidden'},'Total'),null);
  assert.equal(queryNumber({data:{columns:['Total'],rows:[]}},'Total'),null);
  assert.equal(queryNumber({data:{columns:['Other'],rows:[[8]]}},'Total'),null);
});
test('CSV protects against spreadsheet formulas in Graph names and incident titles', () => {
  const csv = tableCsv(['Name','Count'], [['=HYPERLINK("https://evil.test")',2],[' +cmd',3],['normal, "quoted"',4],[-5,5]]);
  assert.ok(csv.includes(`"'=HYPERLINK(""https://evil.test"")"`));
  assert.ok(csv.includes(`"' +cmd"`));
  assert.ok(csv.includes('"normal, ""quoted"""'));
  assert.ok(csv.includes('"-5","5"'));
});
test('control-plane links never accept script schemes or look-alike hosts', () => {
  assert.equal(safeControlPlaneLink({label:'App',url:'https://portal.azure.com/#resource/subscriptions/test'}),true);
  for (const url of ['javascript:alert(1)','https://portal.azure.com.evil.test','https://evil@portal.azure.com','http://portal.azure.com']) assert.equal(safeControlPlaneLink({label:'App',url}),false);
});
test('resource objects remain searchable by display name and details keep full text', () => {
  assert.equal(fullCell({label:'ca-entraguard-portal',url:'https://portal.azure.com'}),'ca-entraguard-portal');
  assert.equal(fullCell('a'.repeat(300)).length,300);
});
