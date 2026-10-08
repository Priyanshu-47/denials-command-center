# ---------------------------------------------------------------------
# Web — two stages: build the bundle, then serve it with nginx.
#
# Nothing in this image touches the data pack, so there is no PHI in any
# layer. The browser only ever talks to this container; nginx forwards
# /api to the API service, which is why the app needs no CORS
# configuration and can never be pointed at a different origin by a
# mis-set environment variable.
# ---------------------------------------------------------------------

FROM node:22-alpine AS build
WORKDIR /app

# Lockfile first: a source edit must not invalidate the package layer.
COPY package.json package-lock.json ./
RUN npm ci --no-fund --no-audit

COPY . .
# tsc runs before vite, so a type error fails the image build rather than
# shipping a bundle that fails at runtime.
RUN npm run build

FROM nginx:1.27-alpine AS runtime

COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html

# nginxinc/nginx-unprivileged is not used, so the master still needs port 80
# — it drops to worker privileges for everything else.
EXPOSE 80

HEALTHCHECK --interval=5s --timeout=3s --retries=12 \
    CMD wget -q -O /dev/null http://127.0.0.1/ || exit 1
