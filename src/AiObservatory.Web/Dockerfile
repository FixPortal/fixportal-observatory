FROM node:24-alpine AS build
WORKDIR /app
COPY package*.json ./
RUN npm ci
COPY . .
# Clear production env vars so .env.production is not baked into the image:
# nginx proxies /api same-origin, no Azure AD sign-in gate needed. VITE_API_KEY is blank
# too: the admin key is added by nginx at runtime, so the bundle and image layers hold no secret.
RUN VITE_API_BASE="" VITE_AAD_CLIENT_ID="" VITE_AAD_TENANT_ID="" VITE_AAD_API_SCOPE="" VITE_API_KEY="" npm run build

FROM nginx:stable-alpine AS runtime
COPY --from=build /app/dist /usr/share/nginx/html
COPY nginx.conf.template /etc/nginx/templates/default.conf.template
ENV NGINX_ENVSUBST_FILTER=OBSERVATORY_API_KEY
EXPOSE 80
