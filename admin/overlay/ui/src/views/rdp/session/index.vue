<template>
  <div class="app-container">
    <el-row :gutter="10" class="mb8">
      <el-col :span="1.5"><el-button icon="Refresh" @click="loadAll">刷新</el-button></el-col>
    </el-row>

    <h4>会话（最近 200）</h4>
    <el-table v-loading="loading" :data="sessions" class="mb8">
      <el-table-column label="状态" prop="State" width="120" />
      <el-table-column label="用户" prop="UserID" :show-overflow-tooltip="true" />
      <el-table-column label="组" prop="GroupID" :show-overflow-tooltip="true" />
      <el-table-column label="资源" prop="ResourceID" :show-overflow-tooltip="true" />
      <el-table-column label="关闭原因" prop="CloseReason" width="160" />
      <el-table-column label="创建时间" prop="CreatedAt" width="180" />
    </el-table>

    <h4>排队中</h4>
    <el-table :data="queues">
      <el-table-column label="组" prop="GroupID" />
      <el-table-column label="用户" prop="UserID" />
      <el-table-column label="状态" prop="Status" width="100" />
      <el-table-column label="序号" prop="Seq" width="100" />
    </el-table>
  </div>
</template>

<script setup name="RdpSession">
import { listSessions, listQueues } from '@/api/rdp/session'

const loading = ref(false)
const sessions = ref([])
const queues = ref([])

async function loadAll() {
  loading.value = true
  try {
    const [s, q] = await Promise.all([listSessions(), listQueues()])
    sessions.value = s.sessions || []
    queues.value = q.queues || []
  } finally {
    loading.value = false
  }
}

loadAll()
</script>
