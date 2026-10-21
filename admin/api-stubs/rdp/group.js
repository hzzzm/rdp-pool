import request from '@/utils/request-pool' // separate axios with admin_token

export function listGroups() {
  return request({ url: '/api/v1/admin/groups', method: 'get' })
}

export function createGroup(data) {
  return request({ url: '/api/v1/admin/groups', method: 'post', data })
}
