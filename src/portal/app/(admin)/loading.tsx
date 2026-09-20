export default function AdminLoading() {
  return <div className="az-content" style={{paddingTop:28}} role="status" aria-label="Loading console data"><h1 style={{fontSize:22,fontWeight:500}}>Loading console data…</h1><p className="admin-meta-note">Reading the connected services. Unavailable sources will be identified individually.</p><div className="az-grid c4">{[0,1,2,3].map(key=><div className="admin-skeleton" key={key}/>)}</div><div className="admin-skeleton" style={{height:260}}/></div>;
}
