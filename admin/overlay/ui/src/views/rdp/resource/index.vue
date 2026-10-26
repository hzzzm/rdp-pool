<template>
  <div class="app-container">
    <el-form :inline="true" class="mb8">
      <el-form-item label="资源组">
        <el-select v-model="groupId" clearable placeholder="全部" style="width: 220px" @change="load">
          <el-option v-for="g in groups" :key="g.ID" :label="g.Name + ' (' + g.Code + ')'" :value="g.ID" />
        </el-select>
      </el-form-item>
      <el-form-item>
        <el-button type="primary" plain icon="Plus" @click="openCreate" v-hasPermi="['rdp:resource:list']">录入资源</el-button>
        <el-button icon="Refresh" @click="load">刷新</el-button>
      </el-form-item>
    </el-form>

    <el-table v-loading="loading" :data="list">
      <el-table-column label="名称" prop="Name" />
      <el-table-column label="资源组" min-width="140">
        <template #default="scope">
          <span>{{ scope.row.GroupName || groupLabel(scope.row.GroupID) }}</span>
        </template>
      </el-table-column>
      <el-table-column label="Host" prop="Host" />
      <el-table-column label="端口" prop="RDPPort" width="80" />
      <el-table-column label="状态" prop="Status" width="100" />
      <el-table-column label="Linger" width="90">
        <template #default="scope">
          <el-tag v-if="scope.row.LingerHint" type="warning">可能仍有旧 RDP</el-tag>
          <span v-else>—</span>
        </template>
      </el-table-column>
      <el-table-column label="操作" width="260" fixed="right">
        <template #default="scope">
          <el-button link type="primary" @click="openEdit(scope.row)">编辑</el-button>
          <el-button link type="primary" @click="openCred(scope.row)">凭据</el-button>
          <el-button link type="warning" @click="doSoftRelease(scope.row)" v-hasPermi="['rdp:resource:softRelease']">软释放</el-button>
        </template>
      </el-table-column>
    </el-table>

    <el-dialog :title="isEdit ? '编辑 Windows 资源' : '录入 Windows 资源'" v-model="formVisible" width="520px" append-to-body>
      <el-form :model="form" label-width="90px">
        <el-form-item label="资源组" required>
          <el-select v-model="form.group_id" style="width: 100%" placeholder="选择归属资源组">
            <el-option v-for="g in groups" :key="g.ID" :label="g.Name + ' (' + g.Code + ')'" :value="g.ID" />
          </el-select>
        </el-form-item>
        <el-form-item label="名称"><el-input v-model="form.name" /></el-form-item>
        <el-form-item label="Host" required><el-input v-model="form.host" placeholder="IP 或主机名" /></el-form-item>
        <el-form-item label="端口"><el-input-number v-model="form.rdp_port" :min="1" :max="65535" /></el-form-item>
        <el-form-item label="启用" v-if="isEdit"><el-switch v-model="form.enabled" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button type="primary" @click="submitForm">确定</el-button>
        <el-button @click="formVisible = false">取消</el-button>
      </template>
    </el-dialog>

    <el-dialog title="设置 RDP 凭据" v-model="credVisible" width="480px" append-to-body>
      <el-form :model="credForm" label-width="90px">
        <el-form-item label="用户名" required><el-input v-model="credForm.username" /></el-form-item>
        <el-form-item label="密码" required><el-input v-model="credForm.password" type="password" show-password /></el-form-item>
      </el-form>
      <template #footer>
        <el-button type="primary" @click="submitCred">保存</el-button>
        <el-button @click="credVisible = false">取消</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup name="RdpResource">
import { listGroups } from '@/api/rdp/group'
import { listResources, createResource, updateResource, softReleaseResource, putResourceCredentials } from '@/api/rdp/resource'
import { ElMessage, ElMessageBox } from 'element-plus'

const loading = ref(false)
const list = ref([])
const groups = ref([])
const groupId = ref('')
const formVisible = ref(false)
const credVisible = ref(false)
const isEdit = ref(false)
const editId = ref('')
const form = reactive({ group_id: '', name: '', host: '', rdp_port: 3389, enabled: true })
const credForm = reactive({ id: '', username: '', password: '' })

function groupLabel(id) {
  const g = groups.value.find((x) => x.ID === id)
  return g ? g.Name : id
}

async function loadGroups() {
  const data = await listGroups()
  groups.value = data.groups || []
}

async function load() {
  loading.value = true
  try {
    const params = {}
    if (groupId.value) params.group_id = groupId.value
    const data = await listResources(params)
    list.value = data.resources || []
  } finally {
    loading.value = false
  }
}

function openCreate() {
  isEdit.value = false
  editId.value = ''
  form.group_id = groupId.value || (groups.value[0] && groups.value[0].ID) || ''
  form.name = ''
  form.host = ''
  form.rdp_port = 3389
  form.enabled = true
  formVisible.value = true
}

function openEdit(row) {
  isEdit.value = true
  editId.value = row.ID
  form.group_id = row.GroupID
  form.name = row.Name
  form.host = row.Host
  form.rdp_port = row.RDPPort || 3389
  form.enabled = row.Enabled !== false
  formVisible.value = true
}

async function submitForm() {
  const payload = {
    group_id: form.group_id,
    name: form.name,
    host: form.host,
    rdp_port: form.rdp_port
  }
  if (isEdit.value) {
    await updateResource(editId.value, { ...payload, enabled: form.enabled })
    ElMessage.success('已更新')
  } else {
    await createResource(payload)
    ElMessage.success('已创建')
  }
  formVisible.value = false
  load()
}

function openCred(row) {
  credForm.id = row.ID
  credForm.username = ''
  credForm.password = ''
  credVisible.value = true
}

async function submitCred() {
  await putResourceCredentials(credForm.id, { username: credForm.username, password: credForm.password })
  ElMessage.success('凭据已保存（AES-GCM）')
  credVisible.value = false
}

async function doSoftRelease(row) {
  await ElMessageBox.confirm(
    '软释放会结束池侧占用，但不会断开现有 RDP；该机将进入组内空闲供给队尾（linger）。确认？',
    '软释放确认',
    { type: 'warning' }
  )
  await softReleaseResource(row.ID)
  ElMessage.success('已软释放')
  load()
}

loadGroups().then(load)
</script>
