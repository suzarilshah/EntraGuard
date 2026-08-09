/**
 * What the security console looks like from the customer's hostname: absent.
 *
 * Deliberately says nothing about what does exist here. A 404 that explains "the admin
 * portal is at another address" is a map for anyone probing.
 */
export default function NotFoundOnTreasury() {
  return (
    <div className="rp">
      <header className="rp-header">
        <span className="rp-logo">CT</span>
        <span>
          <span className="rp-brandname">Contoso Treasury</span>
          <span className="rp-brandsub">Payment Operations</span>
        </span>
      </header>

      <main className="rp-center">
        <div className="rp-card">
          <h1 className="rp-h1">Page not found</h1>
          <p className="rp-sub">That page does not exist.</p>
          <a className="rp-btn block" href="/" style={{ textAlign: 'center', display: 'block' }}>
            Back to Treasury
          </a>
        </div>
      </main>
    </div>
  );
}
