<template>
  <div class="app-container">
    <el-row :gutter="10" class="mb8">
      <el-col :span="1.5">
        <el-button type="primary" plain icon="Plus" @click="openCreate" v-hasPermi="['rdp:user:grantGroup']">新增终端用户</el-button>
      </el-col>
      <el-col :span="1.5">
        <el-button icon="Refresh" @click="load">刷新</el-button>
      </el-col>
    </el-row>

    <el-table v-loading="loading" :data="list">
      <el-table-column label="登录名" prop="Login" />
      <el-table-column label="显示名" prop="DisplayName" />
      <el-table-column label="启用" width="80">
        <template #default="scope">{{ scope.row.Enabled ? '是' : '否' }}</template>
      </el-table-column>
      <el-table-column label="ID" prop="ID" :show-overflow-tooltip="true" />
      <el-table-column label="操作" width="140">
        <template #default="scope">
          <el-button link type="primary" @click="openGrant(scope.row)">组授权</el-button>
        </template>
      </el-table-column>
    </el-table>

    <el-dialog title="新增终端用户" v-model="createVisible" width="480px" append-to-body>
      <el-form :model="createForm" label-width="90px">
        <el-form-item label="登录名" required><el-input v-model="createForm.login" /></el-form-item>
        <el-form-item label="密码" required><el-input v-model="createForm.password" type="password" show-password /></el-form-item>
        <el-form-item label="显示名"><el-input v-model="createForm.display_name" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button type="primary" @click="submitCreate">确定</el-button>
        <el-button @click="createVisible = false">取消</el-button>
      </template>
    </el-dialog>

    <el-dialog title="用户↔组授权" v-model="grantVisible" width="480px" append-to-body>
      <div v-loading="grantLoading">
        <el-checkbox-group v-model="grantIds">
          <el-checkbox v-for="g in groups" :key="g.ID" :label="g.ID">{{ g.Name }} ({{ g.Code }})</el-checkbox>
        </el-checkbox-group>
      </div>
      <template #footer>
        <el-button type="primary" :disabled="grantLoading" @click="submitGrant">保存</el-button>
        <el-button @click="grantVisible = false">取消</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup name="RdpUser">
import { listPoolUsers, createPoolUser, getUserGroups, grantUserGroups } from '@/api/rdp/user'
import { listGroups } from '@/api/rdp/group'
import { ElMessage } from 'element-plus'

const loading = ref(false)
const list = ref([])
const groups = ref([])
const createVisible = ref(false)
const grantVisible = ref(false)
const grantLoading = ref(false)
const createForm = reactive({ login: '', password: '', display_name: '' })
const grantUserId = ref('')
const grantIds = ref([])

async function load() {
  loading.value = true
  try {
    const data = await listPoolUsers()
    list.value = data.users || []
  } finally {
    loading.value = false
  }
}

async function loadGroups() {
  const data = await listGroups()
  groups.value = data.groups || []
}

function openCreate() {
  createForm.login = ''
  createForm.password = ''
  createForm.display_name = ''
  createVisible.value = true
}

async function submitCreate() {
  await createPoolUser({ ...createForm })
  ElMessage.success('已创建')
  createVisible.value = false
  load()
}

async function openGrant(row) {
  grantUserId.value = row.ID
  grantIds.value = []
  grantVisible.value = true
  grantLoading.value = true
  try {
    if (!groups.value.length) await loadGroups()
    const data = await getUserGroups(row.ID)
    grantIds.value = Array.isArray(data.group_ids) ? [...data.group_ids] : []
  } catch (e) {
    grantIds.value = []
  } finally {
    grantLoading.value = false
  }
}

async function submitGrant() {
  await grantUserGroups(grantUserId.value, grantIds.value)
  ElMessage.success('授权已更新')
  grantVisible.value = false
}

loadGroups().then(load)
</script>
