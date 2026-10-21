import request from '@/utils/request-pool'

/** Exchange RuoYi identity for Go admin_token (scaffold). */
export function exchangeAdminToken(data) {
  return request({
    url: '/api/v1/admin/auth/exchange',
    method: 'post',
    data
  })
}
