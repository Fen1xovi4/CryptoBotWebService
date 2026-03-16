import { useEffect } from 'react';
import { Outlet } from 'react-router-dom';
import Sidebar from './Sidebar';
import TopBar from './TopNav';
import { useSubscriptionStore } from '../../stores/subscriptionStore';

export default function MainLayout() {
  const fetchSubscription = useSubscriptionStore((s) => s.fetchSubscription);
  const loaded = useSubscriptionStore((s) => s.loaded);

  useEffect(() => {
    if (!loaded) fetchSubscription();
  }, [loaded, fetchSubscription]);

  return (
    <div className="flex h-screen overflow-hidden bg-bg-primary">
      <Sidebar />
      <div className="flex flex-1 flex-col min-w-0">
        <TopBar />
        <main className="flex-1 overflow-y-auto p-6">
          <Outlet />
        </main>
      </div>
    </div>
  );
}
