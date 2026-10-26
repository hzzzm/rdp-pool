import request from '@/utils/request-pool'

export function listGroups() {
  return request({ url: '/api/v1/admin/groups', method: 'get' })
}

export function createGroup(data) {
  return request({ url: '/api/v1/admin/groups', method: 'post', data })
}

export function updateGroup(id, data) {
  return request({ url: `/api/v1/admin/groups/${id}`, method: 'put', data })
}
