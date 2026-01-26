-- Add Notifications table to a tenant schema (run inside the tenant schema)
-- Usage: replace tenant_xxx with your tenant schema name (e.g., tenant_schoolname)

CREATE TABLE IF NOT EXISTS "Notifications" (
    id serial PRIMARY KEY,
    student_id integer NOT NULL,
    title varchar(255),
    message text NOT NULL,
    type varchar(50) DEFAULT 'General', -- e.g. Progress, Announcement
    is_read boolean DEFAULT false,
    created_at timestamp with time zone DEFAULT now()
);

-- Index on student_id for fast lookups
CREATE INDEX IF NOT EXISTS ix_notifications_student_id ON "Notifications" (student_id);

-- Optional: If you have a students table FK, add a constraint (ensure student's table name)
-- ALTER TABLE "Notifications" ADD CONSTRAINT fk_notifications_student FOREIGN KEY (student_id) REFERENCES "Students"(id) ON DELETE CASCADE;