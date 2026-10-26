<template>
  <div class="app-container">
    <el-row :gutter="10" class="mb8">
      <el-col :span="1.5">
        <el-button type="primary" plain icon="Plus" @click="openCreate" v-hasPermi="['rdp:group:edit']">新增资源组</el-button>
      </el-col>
      <el-col :span="1.5">
        <el-button icon="Refresh" @click="load">刷新</el-button>
      </el-col>
    </el-row>
    <el-table v-loading="loading" :data="list">
      <el-table-column label="编码" prop="Code" />
      <el-table-column label="名称" prop="Name" />
      <el-table-column label="排序" prop="SortOrder" width="80" />
      <el-table-column label="启用" width="80">
        <template #default="scope">{{ scope.row.Enabled ? '是' : '否' }}</template>
      </el-table-column>
      <el-table-column label="说明" prop="Description" :show-overflow-tooltip="true" />
      <el-table-column label="ID" prop="ID" :show-overflow-tooltip="true" />
      <el-table-column label="操作" width="100" fixed="right">
        <template #default="scope">
          <el-button link type="primary" @click="openEdit(scope.row)" v-hasPermi="['rdp:group:edit']">编辑</el-button>
        </template>
      </el-table-column>
    </el-table>

    <el-dialog :title="isEdit ? '编辑资源组' : '新增资源组'" v-model="visible" width="480px" append-to-body>
      <el-form :model="form" label-width="80px">
        <el-form-item label="编码" required><el-input v-model="form.code" placeholder="如 office" :disabled="isEdit" /></el-form-item>
        <el-form-item label="名称" required><el-input v-model="form.name" /></el-form-item>
        <el-form-item label="排序"><el-input-number v-model="form.sort_order" :min="0" /></el-form-item>
        <el-form-item label="启用" v-if="isEdit"><el-switch v-model="form.enabled" /></el-form-item>
        <el-form-item label="说明"><el-input v-model="form.description" type="textarea" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button type="primary" @click="submit">确定</el-button>
        <el-button @click="visible = false">取消</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup name="RdpGroup">
import { listGroups, createGroup, updateGroup } from '@/api/rdp/group'
import { ElMessage } from 'element-plus'

const loading = ref(false)
const list = ref([])
const visible = ref(false)
const isEdit = ref(false)
const editId = ref('')
const form = reactive({ code: '', name: '', sort_order: 0, description: '', enabled: true })

async function load() {
  loading.value = true
  try {
    const data = await listGroups()
    list.value = data.groups || []
  } finally {
    loading.value = false
  }
}

function openCreate() {
  isEdit.value = false
  editId.value = ''
  form.code = ''
  form.name = ''
  form.sort_order = 0
  form.description = ''
  form.enabled = true
  visible.value = true
}

function openEdit(row) {
  isEdit.value = true
  editId.value = row.ID
  form.code = row.Code
  form.name = row.Name
  form.sort_order = row.SortOrder ?? 0
  form.description = row.Description || ''
  form.enabled = !!row.Enabled
  visible.value = true
}

async function submit() {
  if (isEdit.value) {
    await updateGroup(editId.value, {
      code: form.code,
      name: form.name,
      sort_order: form.sort_order,
      description: form.description,
      enabled: form.enabled
    })
    ElMessage.success('已更新')
  } else {
    await createGroup({
      code: form.code,
      name: form.name,
      sort_order: form.sort_order,
      description: form.description
    })
    ElMessage.success('已创建')
  }
  visible.value = false
  load()
}

load()
</script>
