import { lazy, Suspense } from 'react'
import { createBrowserRouter } from 'react-router'
import { Loading } from './components/Feedback'
import { MissionPage } from './features/missions/MissionPage'
import { MissionsPage } from './features/missions/MissionsPage'
import { AppShell } from './components/AppShell'
import { EnrolTwoFactorPage } from './features/auth/EnrolTwoFactorPage'
import { InvitePage } from './features/auth/InvitePage'
import { LoginPage } from './features/auth/LoginPage'
import { ResetPage } from './features/auth/ResetPage'
import { SetupPage } from './features/auth/SetupPage'
import { TwoFactorLoginPage } from './features/auth/TwoFactorLoginPage'
import { DashboardPage } from './features/dashboard/DashboardPage'
import { AuditPage } from './features/members/AuditPage'
import { InvitesPage } from './features/members/InvitesPage'
import { MembersLayout, MembersPage } from './features/members/MembersPage'
import { RolesPage } from './features/members/RolesPage'
import { ProfileLayout } from './features/profiles/ProfileLayout'
import { ProfilesPage } from './features/profiles/ProfilesPage'
import { ModsTab } from './features/profiles/ModsTab'
import { ModsPage } from './features/mods/ModsPage'
import { CollectionsPage } from './features/mods/CollectionsPage'
import { CollectionEditor } from './features/mods/CollectionEditor'
import { GeneralTab, PerformanceTab, ProfileOverview, ServerConfigTab } from './features/profiles/ProfileTabs'
import { DifficultyTab, MissionsTab, StartupTab } from './features/profiles/ConfigTabs'
import { ComparePage, PresetsPage, RevisionsTab } from './features/profiles/HistoryPages'
import { HeadlessTab } from './features/profiles/HeadlessTab'
import { LogsPage } from './features/server/LogsPage'
import { PlayersPage } from './features/server/PlayersPage'
import { BackupsPage } from './features/backups/BackupsPage'
import { SchedulesPage } from './features/schedules/SchedulesPage'
import { ServerLayout } from './features/server/ServerLayout'
import { ServerMissionsPage } from './features/server/ServerMissionsPage'
import { ServerPage } from './features/server/ServerPage'
import { ServerSetupPage } from './features/server/SetupPage'
import { SettingsPage } from './features/settings/SettingsPage'
import { FilesPage } from './features/files/FilesPage'
import { OperationsPage } from './features/operations/OperationsPage'
import { OperationPage } from './features/operations/OperationPage'
import { RequireAuth, RequirePermission } from './lib/auth'

// Monaco is large: the editor and its chunk load only when a mission's files are opened.
const EditorPage = lazy(() => import('./features/missions/EditorPage').then(m => ({ default: m.EditorPage })))

export const router = createBrowserRouter([
  { path: '/setup', element: <SetupPage /> },
  { path: '/login', element: <LoginPage /> },
  { path: '/login/2fa', element: <TwoFactorLoginPage /> },
  { path: '/invite/:token', element: <InvitePage /> },
  { path: '/reset', element: <ResetPage /> },
  {
    element: <RequireAuth />,
    children: [
      {
        element: <AppShell />,
        children: [
          { index: true, handle: { title: 'Dashboard' }, element: <DashboardPage /> },
          {
            path: 'members',
            handle: { title: 'Members' },
            element: <MembersLayout />,
            children: [
              { index: true, element: <MembersPage /> },
              { path: 'roles', handle: { title: 'Roles' }, element: <RolesPage /> },
              { path: 'invites', handle: { title: 'Invites' }, element: <RequirePermission perm="members.manage"><InvitesPage /></RequirePermission> },
              { path: 'audit', handle: { title: 'Audit log' }, element: <RequirePermission perm="audit.view"><AuditPage /></RequirePermission> },
            ],
          },
          {
            path: 'server',
            handle: { title: 'Server' },
            element: <ServerLayout />,
            children: [
              { index: true, element: <ServerPage /> },
              { path: 'missions', handle: { title: 'Server missions' }, element: <RequirePermission perm="missions.view"><ServerMissionsPage /></RequirePermission> },
              { path: 'logs', handle: { title: 'Server logs' }, element: <RequirePermission perm="logs.view"><LogsPage /></RequirePermission> },
              { path: 'players', handle: { title: 'Players' }, element: <RequirePermission perm="server.view"><PlayersPage /></RequirePermission> },
              { path: 'setup', handle: { title: 'Server setup' }, element: <ServerSetupPage /> },
            ],
          },
          { path: 'operations', handle: { title: 'Operations' }, element: <RequirePermission perm="operations.view"><OperationsPage /></RequirePermission> },
          { path: 'operations/:id', handle: { title: 'Operation' }, element: <RequirePermission perm="operations.view"><OperationPage /></RequirePermission> },
          { path: 'files', handle: { title: 'Files' }, element: <RequirePermission perm="files.*"><FilesPage /></RequirePermission> },
          { path: 'backups', handle: { title: 'Backups' }, element: <RequirePermission perm="backups.manage"><BackupsPage /></RequirePermission> },
          { path: 'schedules', handle: { title: 'Schedules' }, element: <RequirePermission perm="schedules.manage"><SchedulesPage /></RequirePermission> },
          { path: 'profiles', handle: { title: 'Profiles' }, element: <ProfilesPage /> },
          { path: 'profiles/compare', handle: { title: 'Compare profiles' }, element: <ComparePage /> },
          { path: 'profiles/presets', handle: { title: 'Difficulty presets' }, element: <PresetsPage /> },
          {
            path: 'profiles/:id',
            handle: { title: 'Profile' },
            element: <RequirePermission perm="profiles.view"><ProfileLayout /></RequirePermission>,
            children: [
              { index: true, element: <ProfileOverview /> },
              { path: 'general', element: <GeneralTab /> },
              { path: 'server-config', element: <ServerConfigTab /> },
              { path: 'performance', element: <PerformanceTab /> },
              { path: 'difficulty', element: <DifficultyTab /> },
              { path: 'missions', element: <MissionsTab /> },
              { path: 'startup', element: <StartupTab /> },
              { path: 'headless', element: <HeadlessTab /> },
              { path: 'mods', element: <ModsTab /> },
              { path: 'revisions', element: <RevisionsTab /> },
            ],
          },
          { path: 'missions', handle: { title: 'Missions' }, element: <MissionsPage /> },
          { path: 'missions/:id', handle: { title: 'Mission' }, element: <MissionPage /> },
          { path: 'missions/:id/edit', handle: { title: 'Mission files' }, element: <Suspense fallback={<Loading />}><EditorPage /></Suspense> },
          { path: 'mods', handle: { title: 'Mods' }, element: <ModsPage /> },
          { path: 'mods/lists', handle: { title: 'Mod lists' }, element: <CollectionsPage /> },
          { path: 'mods/lists/:id', handle: { title: 'Mod list' }, element: <CollectionEditor /> },
          { path: 'settings', handle: { title: 'Settings' }, element: <SettingsPage /> },
          { path: 'account/2fa', handle: { title: 'Two-factor sign-in' }, element: <EnrolTwoFactorPage /> },
          { path: '*', handle: { title: 'Not found' }, element: <p className="m-0 text-text-muted">That page doesn't exist.</p> },
        ],
      },
    ],
  },
])
