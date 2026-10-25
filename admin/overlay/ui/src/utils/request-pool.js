/**
 * Axios for Go pool Admin API (/pool-api). Uses admin_token only — never RuoYi Admin-Token.
 */
import axios from 'axios'
import { ElMessage } from 'element-plus'
import { getToken } from '@/utils/auth'
import { getAdminToken, setAdminToken, removeAdminToken } from '@/utils/auth-pool'

const service = axios.create({
  baseURL: '/pool-api',
  timeout: 20000
})

let exchanging = null

async function ensureAdminToken() {
  const existing = getAdminToken()
  if (existing) return existing
  if (!exchanging) {
    exchanging = axios
      .post('/pool-api/api/v1/admin/auth/exchange', {
        ruoyi_token: getToken() || '',
        admin_id: 'admin',
        login: 'admin'
      })
      .then((res) => {
        const tok = res.data?.admin_token
        if (!tok) throw new Error('exchange failed')
        setAdminToken(tok)
        return tok
      })
      .finally(() => {
        exchanging = null
      })
  }
  return exchanging
}

service.interceptors.request.use(
  async (config) => {
    const token = await ensureAdminToken()
    config.headers['Authorization'] = 'Bearer ' + token
    const ry = getToken()
    if (ry) config.headers['X-RuoYi-Token'] = ry
    return config
  },
  (error) => Promise.reject(error)
)

service.interceptors.response.use(
  (res) => res.data,
  (error) => {
    const status = error.response?.status
    const msg = error.response?.data?.error || error.message || 'pool-api error'
    if (status === 401) {
      removeAdminToken()
    }
    ElMessage.error(msg)
    return Promise.reject(error)
  }
)

export default service
