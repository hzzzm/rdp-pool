-- RDP pool menus for RuoYi (menu_id 2100+). Admin user_id=1 sees all menus via isAdmin().
-- Run against database `ruoyi` after official schema import.

USE ruoyi;

INSERT INTO sys_menu VALUES('2100', '远程桌面资源池', '0', '5', 'rdp', NULL, '', '', 1, 0, 'M', '0', '0', '', 'monitor', 'admin', sysdate(), '', NULL, 'RDP pool');
INSERT INTO sys_menu VALUES('2101', '资源组', '2100', '1', 'group', 'rdp/group/index', '', '', 1, 0, 'C', '0', '0', 'rdp:group:list', 'tree', 'admin', sysdate(), '', NULL, '');
INSERT INTO sys_menu VALUES('2102', 'Windows 资源', '2100', '2', 'resource', 'rdp/resource/index', '', '', 1, 0, 'C', '0', '0', 'rdp:resource:list', 'server', 'admin', sysdate(), '', NULL, '');
INSERT INTO sys_menu VALUES('2103', '终端用户', '2100', '3', 'pool-user', 'rdp/user/index', '', '', 1, 0, 'C', '0', '0', 'rdp:user:grantGroup', 'user', 'admin', sysdate(), '', NULL, '');
INSERT INTO sys_menu VALUES('2104', '会话与排队', '2100', '4', 'session', 'rdp/session/index', '', '', 1, 0, 'C', '0', '0', 'rdp:resource:list', 'log', 'admin', sysdate(), '', NULL, '');
INSERT INTO sys_menu VALUES('2105', '池配置', '2100', '5', 'settings', 'rdp/settings/index', '', '', 1, 0, 'C', '0', '0', 'rdp:config:edit', 'edit', 'admin', sysdate(), '', NULL, '');

-- Buttons
INSERT INTO sys_menu VALUES('2111', '资源组编辑', '2101', '1', '', '', '', '', 1, 0, 'F', '0', '0', 'rdp:group:edit', '#', 'admin', sysdate(), '', NULL, '');
INSERT INTO sys_menu VALUES('2112', '软释放', '2102', '1', '', '', '', '', 1, 0, 'F', '0', '0', 'rdp:resource:softRelease', '#', 'admin', sysdate(), '', NULL, '');

-- Common role (role_id=2) also gets pool menus
INSERT INTO sys_role_menu VALUES ('2', '2100');
INSERT INTO sys_role_menu VALUES ('2', '2101');
INSERT INTO sys_role_menu VALUES ('2', '2102');
INSERT INTO sys_role_menu VALUES ('2', '2103');
INSERT INTO sys_role_menu VALUES ('2', '2104');
INSERT INTO sys_role_menu VALUES ('2', '2105');
INSERT INTO sys_role_menu VALUES ('2', '2111');
INSERT INTO sys_role_menu VALUES ('2', '2112');
