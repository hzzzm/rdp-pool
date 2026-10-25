<template>
  <div class="app-container">
    <el-form :model="form" label-width="200px" style="max-width: 640px">
      <el-form-item label="最长占用 TTL（秒）">
        <el-input v-model="form.max_occupation_ttl_sec" placeholder="如 14400" />
      </el-form-item>
      <el-form-item label="心跳租约 TTL（秒）">
        <el-input v-model="form.lease_ttl_sec" />
      </el-form-item>
      <el-form-item label="待确认超时（秒）">
        <el-input v-model="form.pending_confirm_sec" />
      </el-form-item>
      <el-form-item>
        <el-button type="primary" @click="save" v-hasPermi="['rdp:config:edit']">保存（写池库）</el-button>
        <el-button @click="load">重新加载</el-button>
      </el-form-item>
    </el-form>

    <h4 class="mt20">池审计（最近）</h4>
    <el-table :data="audit" max-height="360">
      <el-table-column label="时间" prop="At" width="180" />
      <el-table-column label="动作" prop="Action" width="160" />
      <el-table-column label="操作者" prop="ActorID" />
      <el-table-column label="目标" prop="TargetID" />
    </el-table>
  </div>
</template>

<script setup name="RdpSettings">
import { getSettings, putSettings, listAudit } from '@/api/rdp/settings'
import { ElMessage } from 'element-plus'

const form = reactive({
  max_occupation_ttl_sec: '',
  lease_ttl_sec: '',
  pending_confirm_sec: ''
})
const audit = ref([])

async function load() {
  const data = await getSettings()
  const s = data.settings || {}
  form.max_occupation_ttl_sec = s.max_occupation_ttl_sec || ''
  form.lease_ttl_sec = s.lease_ttl_sec || ''
  form.pending_confirm_sec = s.pending_confirm_sec || ''
  const a = await listAudit()
  audit.value = a.audit || a.logs || []
}

async function save() {
  const body = {}
  Object.keys(form).forEach((k) => {
    if (form[k] !== '' && form[k] != null) body[k] = String(form[k])
  })
  await putSettings(body)
  ElMessage.success('已保存')
  load()
}

load()
</script>
