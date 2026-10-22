-- MySQL 8.0 / MariaDB 10.6+. Use a dedicated database; no driver binaries are stored here.
CREATE TABLE IF NOT EXISTS uda_schema (
  id INT PRIMARY KEY,
  version INT NOT NULL
) ENGINE=InnoDB;
INSERT IGNORE INTO uda_schema(id,version) VALUES(1,1);
CREATE TABLE IF NOT EXISTS uda_documents (
  repository_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  document_key VARCHAR(512) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
  body LONGTEXT CHARACTER SET utf8mb4 NOT NULL,
  updated_utc DATETIME(6) NOT NULL,
  PRIMARY KEY(repository_id,document_key)
) ENGINE=InnoDB;
