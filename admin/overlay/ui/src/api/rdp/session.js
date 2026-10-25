import request from '@/utils/request-pool'

export function listSessions(params) {
  return request({ url: '/api/v1/admin/sessions', method: 'get', params })
}

export function listQueues() {
  return request({ url: '/api/v1/admin/queues', method: 'get' })
}
