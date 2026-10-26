import request from '@/utils/request-pool'

export function getSettings() {
  return request({ url: '/api/v1/admin/settings', method: 'get' })
}

export function putSettings(data) {
  return request({ url: '/api/v1/admin/settings', method: 'put', data })
}

export function listAudit() {
  return request({ url: '/api/v1/admin/audit', method: 'get' })
}
