import request from '@/utils/request-pool'

export function listPoolUsers() {
  return request({ url: '/api/v1/admin/pool-users', method: 'get' })
}

export function createPoolUser(data) {
  return request({ url: '/api/v1/admin/pool-users', method: 'post', data })
}

export function getUserGroups(userId) {
  return request({ url: `/api/v1/admin/pool-users/${userId}/groups`, method: 'get' })
}

export function grantUserGroups(userId, groupIds) {
  return request({ url: `/api/v1/admin/pool-users/${userId}/groups`, method: 'put', data: { group_ids: groupIds } })
}
