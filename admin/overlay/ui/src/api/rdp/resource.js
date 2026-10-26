import request from '@/utils/request-pool'

export function listResources(params) {
  return request({ url: '/api/v1/admin/resources', method: 'get', params })
}

export function createResource(data) {
  return request({ url: '/api/v1/admin/resources', method: 'post', data })
}

export function softReleaseResource(id) {
  return request({ url: `/api/v1/admin/resources/${id}/soft-release`, method: 'post' })
}

export function putResourceCredentials(id, data) {
  return request({ url: `/api/v1/admin/resources/${id}/credentials`, method: 'put', data })
}
