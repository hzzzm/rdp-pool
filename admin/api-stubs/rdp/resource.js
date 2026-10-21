import request from '@/utils/request-pool'

export function listResources(query) {
  return request({ url: '/api/v1/admin/resources', method: 'get', params: query })
}

export function createResource(data) {
  return request({ url: '/api/v1/admin/resources', method: 'post', data })
}

export function softRelease(resourceId) {
  return request({
    url: `/api/v1/admin/resources/${resourceId}/soft-release`,
    method: 'post'
  })
}
