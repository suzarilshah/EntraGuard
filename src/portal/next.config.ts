import type { NextConfig } from 'next';

const config: NextConfig = {
  // Produces a self-contained server bundle for the container image.
  output: 'standalone',
  reactStrictMode: true,
  // The Azure SDKs are server-only; keeping them external stops the bundler from
  // trying to trace their optional native dependencies into the client build.
  serverExternalPackages: ['@azure/identity', '@azure/monitor-query-logs'],
};

export default config;
