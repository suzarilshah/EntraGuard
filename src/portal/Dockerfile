# Build context is src/portal.
#
# Node 22, not 20, and all three stages must agree.
#
# Eleven Azure packages — @azure/communication-common, @azure/core-rest-pipeline,
# @typespec/ts-http-runtime among them — now declare "node": ">=22.0.0". On node 20 npm ci
# printed EBADENGINE for each and installed them anyway, because engine-strict is off. The
# build therefore succeeded while shipping packages onto a runtime they say they do not
# support.
#
# That is worse than a build failure. @azure/communication-common mints the ACS token for
# the browser soft-phone, and a package free to use APIs node 20 lacks fails at the moment
# it is called rather than at the moment it is installed — which presents as the phone not
# getting a token, and sends whoever debugs it looking at ACS rather than at a base image.
#
# Node 20 also reaches end of life shortly, so this only gets worse by waiting.
FROM node:22-alpine AS deps
WORKDIR /app
COPY package.json package-lock.json* ./
RUN npm ci --no-audit --no-fund

FROM node:22-alpine AS build
WORKDIR /app
COPY --from=deps /app/node_modules ./node_modules
COPY . .
ENV NEXT_TELEMETRY_DISABLED=1
RUN npm run build

# Must match the build stage. A standalone bundle built on one major and run on another is
# a class of failure that only shows up at runtime.
FROM node:22-alpine AS runtime
WORKDIR /app
ENV NODE_ENV=production
ENV NEXT_TELEMETRY_DISABLED=1
ENV PORT=3000

RUN addgroup -g 1001 -S nodejs && adduser -S nextjs -u 1001

# `output: 'standalone'` emits a self-contained server plus only the node_modules it
# actually needs, which keeps the image small enough to push quickly over conference wifi.
COPY --from=build --chown=nextjs:nodejs /app/.next/standalone ./
COPY --from=build --chown=nextjs:nodejs /app/.next/static ./.next/static
COPY --from=build --chown=nextjs:nodejs /app/public ./public

USER nextjs
EXPOSE 3000

CMD ["node", "server.js"]
