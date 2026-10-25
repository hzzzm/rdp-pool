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
      <el-table-column label="Host" prop="Host" />
      <el-table-column label="端口" prop="RDPPort" width="80" />
      <el-table-column label="状态" prop="Status" width="100" />
      <el-table-column label="Linger" width="90">
        <template #default="scope">
          <el-tag v-if="scope.row.LingerHint" type="warning">可能仍有旧 RDP</el-tag>
          <span v-else>—</span>
        </template>
      </el-table-column>
      <el-table-column label="组 ID" prop="GroupID" :show-overflow-tooltip="true" />
      <el-table-column label="操作" width="220" fixed="right">
        <template #default="scope">
          <el-button link type="primary" @click="openCred(scope.row)">凭据</el-button>
          <el-button link type="warning" @click="doSoftRelease(scope.row)" v-hasPermi="['rdp:resource:softRelease']">软释放</el-button>
        </template>
      </el-table-column>
    </el-table>

    <el-dialog title="录入 Windows 资源" v-model="createVisible" width="520px" append-to-body>
      <el-form :model="createForm" label-width="90px">
        <el-form-item label="资源组" required>
          <el-select v-model="createForm.group_id" style="width: 100%">
            <el-option v-for="g in groups" :key="g.ID" :label="g.Name" :value="g.ID" />
          </el-select>
        </el-form-item>
        <el-form-item label="名称"><el-input v-model="createForm.name" /></el-form-item>
        <el-form-item label="Host" required><el-input v-model="createForm.host" placeholder="IP 或主机名" /></el-form-item>
        <el-form-item label="端口"><el-input-number v-model="createForm.rdp_port" :min="1" :max="65535" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button type="primary" @click="submitCreate">确定</el-button>
        <el-button @click="createVisible = false">取消</el-button>
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
import { listResources, createResource, softReleaseResource, putResourceCredentials } from '@/api/rdp/resource'
import { ElMessage, ElMessageBox } from 'element-plus'

const loading = ref(false)
const list = ref([])
const groups = ref([])
const groupId = ref('')
const createVisible = ref(false)
const credVisible = ref(false)
const createForm = reactive({ group_id: '', name: '', host: '', rdp_port: 3389 })
const credForm = reactive({ id: '', username: '', password: '' })

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
  createForm.group_id = groupId.value || (groups.value[0] && groups.value[0].ID) || ''
  createForm.name = ''
  createForm.host = ''
  createForm.rdp_port = 3389
  createVisible.value = true
}

async function submitCreate() {
  await createResource({ ...createForm })
  ElMessage.success('已创建')
  createVisible.value = false
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
