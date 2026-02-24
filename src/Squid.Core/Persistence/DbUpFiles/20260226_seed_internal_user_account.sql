INSERT INTO user_account (
    id,
    user_name,
    normalized_user_name,
    display_name,
    password_hash,
    is_disabled,
    is_system,
    created_at_utc,
    updated_at_utc
)
SELECT
    8888,
    'internal_user',
    'INTERNAL_USER',
    'internal_user',
    '',
    FALSE,
    TRUE,
    NOW(),
    NOW()
WHERE NOT EXISTS (
    SELECT 1
    FROM user_account
    WHERE id = 8888 OR normalized_user_name = 'INTERNAL_USER'
);
