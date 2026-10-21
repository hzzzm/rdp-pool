-- RuoYi menu seed (adjust parent_id / menu_id to fit your sys_menu sequence)
-- Directory: 远程桌面资源池

-- Example only — run after importing official RuoYi schema.
INSERT INTO sys_menu (menu_name, parent_id, order_num, path, component, is_frame, is_cache, menu_type, visible, status, perms, icon, create_by, create_time, remark)
VALUES ('远程桌面资源池', 0, 5, 'rdp', NULL, 1, 0, 'M', '0', '0', '', 'monitor', 'admin', NOW(), 'RDP pool');

-- Submenus (parent_id must be the id of the directory row above)
-- 资源组 / 资源 / 用户授权 / 会话 / 排队 / 软释放入口 / 池配置 / 审计
-- Use RuoYi UI "菜单管理" to wire after first import if IDs collide.
