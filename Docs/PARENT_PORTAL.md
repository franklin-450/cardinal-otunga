Parent Portal Feature

Overview
- Parents can log in using student's account number and name
- View student details, fees, medical info
- Initiate payments via the simulated M-Pesa flow (replace with real integration later)
- View notifications about student progress and announcements
- Optional: real-time notifications via SignalR hub

Setup
1. For each tenant (schema), run `Scripts/tenant_create_notifications.sql` to add the `Notifications` table.
2. If you want real-time notifications, ensure SignalR is enabled (it is by default in `Program.cs`) and the `NotificationHub` is mapped to `/notificationsHub`.

Manual test plan
- Start app locally (dotnet run)
- Create or use an existing tenant with students and grades seeded
- Visit `{schoolName}/parent`, login with a student's account number and name
- Confirm fees and student details display
- Click "Pay Fees" and follow simulated M-Pesa flow; confirm receipt shows and Payments table has a new record
- Create a notification using psql or by POSTing to `{schoolName}/parent/api/notifications/create` with JSON { studentId, title, message, type }
- Refresh parent portal or set up SignalR to see the notification appear

Notes & TODOs
- Replace simulated M-Pesa integration with Daraja API and implement callback to update Payments status securely
- Add server-side admin UI to create notifications (Platform admin or teacher role)
- Add pagination and filters for notifications
- Consider adding notification read receipts and email/SMS delivery options