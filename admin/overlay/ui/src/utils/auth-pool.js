import Cookies from 'js-cookie'

const AdminPoolTokenKey = 'Pool-Admin-Token'

export function getAdminToken() {
  return Cookies.get(AdminPoolTokenKey)
}

export function setAdminToken(token) {
  return Cookies.set(AdminPoolTokenKey, token)
}

export function removeAdminToken() {
  return Cookies.remove(AdminPoolTokenKey)
}
