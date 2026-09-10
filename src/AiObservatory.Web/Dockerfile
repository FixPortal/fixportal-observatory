FROM node:24-alpine AS build
WORKDIR /app
COPY package*.json ./
RUN npm ci
COPY . .
ARG VITE_API_KEY=""
# Clear production env vars so .env.production is not baked into the image:
# nginx proxies /api same-origin, no Azure AD sign-in gate needed.
RUN VITE_API_BASE="" VITE_AAD_CLIENT_ID="" VITE_AAD_TENANT_ID="" VITE_AAD_API_SCOPE="" VITE_API_KEY="$VITE_API_KEY" npm run build

FROM nginx:stable-alpine AS runtime
COPY --from=build /app/dist /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
