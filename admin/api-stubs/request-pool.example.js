/**
 * Example: copy into RuoYi-Vue as src/utils/request-pool.js
 * Base URL should point at Nginx /pool-api (or Go :8081 in local dev).
 */
import axios from 'axios'
import { getAdminToken } from '@/utils/auth-pool'

const service = axios.create({
  baseURL: import.meta.env.VITE_POOL_API || '/pool-api',
  timeout: 15000
})

service.interceptors.request.use(config => {
  const token = getAdminToken()
  if (token) {
    config.headers['Authorization'] = 'Bearer ' + token
  }
  return config
})

export default service
