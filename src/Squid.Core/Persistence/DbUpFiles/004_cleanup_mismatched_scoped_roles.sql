-- One-time cleanup: delete scoped_user_role records where the role's permission scope
-- doesn't match the assignment level (space-scoped role assigned at system level or vice versa).
-- This replaces the runtime CleanupMismatchedScopedRolesAsync that ran on every startup.

WITH permission_categories AS (
    SELECT urp.user_role_id,
           bool_or(urp.permission IN (
               'ProjectView','ProjectCreate','ProjectEdit','ProjectDelete',
               'ProcessView','ProcessEdit','VariableView','VariableEdit',
               'ReleaseView','ReleaseCreate','ReleaseEdit','ReleaseDelete',
               'DeploymentView','DeploymentCreate','DeploymentDelete',
               'EnvironmentView','EnvironmentCreate','EnvironmentEdit','EnvironmentDelete',
               'MachineView','MachineCreate','MachineEdit','MachineDelete',
               'AccountView','AccountCreate','AccountEdit','AccountDelete',
               'FeedView','FeedEdit',
               'LifecycleView','LifecycleCreate','LifecycleEdit','LifecycleDelete',
               'ChannelView','ChannelCreate','ChannelEdit','ChannelDelete',
               'InterruptionView','InterruptionSubmit'
           )) AS has_space_only,
           bool_or(urp.permission IN (
               'UserView','UserEdit','UserRoleView','UserRoleEdit',
               'SpaceView','SpaceCreate','SpaceEdit','SpaceDelete',
               'AdministerSystem'
           )) AS has_system_only,
           bool_or(urp.permission IN (
               'TaskView','TaskCreate','TaskCancel',
               'TeamView','TeamCreate','TeamEdit','TeamDelete'
           )) AS has_mixed
    FROM user_role_permission urp
    GROUP BY urp.user_role_id
),
role_scope AS (
    SELECT user_role_id,
           (has_space_only OR (NOT has_space_only AND NOT has_system_only AND has_mixed)) AS can_space,
           (has_system_only OR (NOT has_space_only AND NOT has_system_only AND has_mixed)) AS can_system
    FROM permission_categories
),
mismatched AS (
    SELECT sur.id FROM scoped_user_role sur
    JOIN role_scope rs ON rs.user_role_id = sur.user_role_id
    WHERE (sur.space_id IS NOT NULL AND NOT rs.can_space)
       OR (sur.space_id IS NULL AND NOT rs.can_system)
)
DELETE FROM scoped_user_role WHERE id IN (SELECT id FROM mismatched);
