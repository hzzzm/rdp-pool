-- Same MySQL instance, two databases (architecture §2.1.1)
CREATE DATABASE IF NOT EXISTS rdp_pool
  DEFAULT CHARACTER SET utf8mb4
  DEFAULT COLLATE utf8mb4_unicode_ci;

CREATE DATABASE IF NOT EXISTS ruoyi
  DEFAULT CHARACTER SET utf8mb4
  DEFAULT COLLATE utf8mb4_unicode_ci;

-- App user is created by MYSQL_USER env; ensure grants on both DBs when using root scripts.
GRANT ALL PRIVILEGES ON rdp_pool.* TO 'rdp_app'@'%';
FLUSH PRIVILEGES;
