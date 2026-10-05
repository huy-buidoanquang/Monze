-- The public role module supports only on-join and tenure automation.
DELETE FROM role_rule
WHERE rule_kind IN ('self_select', 'existing_role');
