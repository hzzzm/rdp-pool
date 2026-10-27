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
    <el-table :data="queues" class="mb8">
      <el-table-column label="资源组" min-width="140">
        <template #default="scope">
          <span>{{ scope.row.GroupName || scope.row.GroupID || '—' }}</span>
        </template>
      </el-table-column>
      <el-table-column label="用户" min-width="120">
        <template #default="scope">
          <span>{{ scope.row.UserLogin || scope.row.UserID }}</span>
        </template>
      </el-table-column>
      <el-table-column label="状态" prop="Status" width="100" />
      <el-table-column label="序号" prop="Seq" width="80" />
      <el-table-column label="排队开始时间" prop="CreatedAt" width="180" />
      <el-table-column label="排队时长" width="120">
        <template #default="scope">
          <span>{{ formatDuration(liveWaitSec(scope.row)) }}</span>
        </template>
      </el-table-column>
    </el-table>

    <h4>排队统计（按资源组）</h4>
    <el-table :data="stats">
      <el-table-column label="资源组" min-width="160">
        <template #default="scope">
          <span>{{ scope.row.GroupName }}</span>
          <span v-if="scope.row.GroupCode" style="color:#909399;margin-left:6px">({{ scope.row.GroupCode }})</span>
        </template>
      </el-table-column>
      <el-table-column label="近1个月排队次数" prop="MonthCount" width="140" />
      <el-table-column label="近1个月平均等候" width="150">
        <template #default="scope">
          <span>{{ formatDuration(Math.round(scope.row.MonthAvgWaitSec || 0)) }}</span>
        </template>
      </el-table-column>
      <el-table-column label="近1周排队次数" prop="WeekCount" width="130" />
      <el-table-column label="近1周平均等候" width="150">
        <template #default="scope">
          <span>{{ formatDuration(Math.round(scope.row.WeekAvgWaitSec || 0)) }}</span>
        </template>
      </el-table-column>
    </el-table>
  </div>
</template>

<script setup name="RdpSession">
import { listSessions, listQueues, listQueueStats } from '@/api/rdp/session'

const loading = ref(false)
const sessions = ref([])
const queues = ref([])
const stats = ref([])
const nowTick = ref(Date.now())

let tickTimer = null
onMounted(() => {
  tickTimer = setInterval(() => { nowTick.value = Date.now() }, 1000)
})
onUnmounted(() => {
  if (tickTimer) clearInterval(tickTimer)
})

function liveWaitSec(row) {
  if (row.CreatedAt) {
    const start = new Date(row.CreatedAt).getTime()
    if (!Number.isNaN(start)) {
      return Math.max(0, Math.floor((nowTick.value - start) / 1000))
    }
  }
  return row.WaitSec || 0
}

function formatDuration(sec) {
  const s = Math.max(0, Number(sec) || 0)
  if (s < 60) return `${s} 秒`
  const m = Math.floor(s / 60)
  const r = s % 60
  if (m < 60) return r ? `${m} 分 ${r} 秒` : `${m} 分`
  const h = Math.floor(m / 60)
  const rm = m % 60
  return rm ? `${h} 小时 ${rm} 分` : `${h} 小时`
}

async function loadAll() {
  loading.value = true
  try {
    const [s, q, st] = await Promise.all([listSessions(), listQueues(), listQueueStats()])
    sessions.value = s.sessions || []
    queues.value = q.queues || []
    stats.value = st.stats || []
  } finally {
    loading.value = false
  }
}

loadAll()
</script>
