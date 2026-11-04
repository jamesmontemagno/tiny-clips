use std::{
    collections::{BTreeMap, BTreeSet},
    sync::LazyLock,
};

use serde::Deserialize;
use serde_json::Value;

use crate::ApiError;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct Contract {
    pub protocol_version: u32,
    pub scopes: BTreeMap<String, String>,
    pub sensitive_scopes: BTreeSet<String>,
    pub session_commands: Vec<String>,
    commands: Vec<Command>,
    pub errors: BTreeMap<String, String>,
}

#[derive(Deserialize)]
pub(crate) struct Command {
    pub name: String,
    pub scope: String,
    params: BTreeMap<String, Value>,
}

pub(crate) static CONTRACT: LazyLock<Contract> = LazyLock::new(|| {
    serde_json::from_str(include_str!(concat!(env!("OUT_DIR"), "/commands.json")))
        .expect("invalid shared automation contract")
});

pub(crate) fn command(name: &str) -> Option<&'static Command> {
    CONTRACT.commands.iter().find(|c| c.name == name)
}

#[derive(Debug, PartialEq)]
pub(crate) enum Decision {
    Allow,
    Approve,
    Deny(&'static str),
}

pub(crate) fn decide(enabled: bool, scopes: Option<&[String]>, name: &str) -> Decision {
    if name == "session.hello" {
        return Decision::Allow;
    }
    if !enabled {
        return Decision::Deny("automation_disabled");
    }
    if CONTRACT.session_commands.iter().any(|c| c == name) {
        return Decision::Allow;
    }
    let Some(scopes) = scopes else {
        return Decision::Deny("unauthorized");
    };
    let Some(command) = command(name) else {
        return Decision::Deny("unknown_command");
    };
    if !scopes.contains(&command.scope) {
        return Decision::Deny("missing_scope");
    }
    if CONTRACT.sensitive_scopes.contains(&command.scope) {
        Decision::Approve
    } else {
        Decision::Allow
    }
}

// Validate the field constraints used by the shared command contract. Semantic checks
// (e.g. a currently authorized display ID) belong to the same services as the UI.
fn matches_field(value: &Value, spec: &Value) -> bool {
    let typed = match spec["type"].as_str() {
        Some("string") => value.is_string(),
        Some("integer") => value.is_i64() || value.is_u64(),
        Some("number") => value.is_number(),
        Some("boolean") => value.is_boolean(),
        Some("any") => true,
        Some("array") => value.as_array().is_some_and(|items| {
            items.len() as u64 >= spec["minItems"].as_u64().unwrap_or(0)
                && spec
                    .get("items")
                    .is_none_or(|s| items.iter().all(|v| matches_field(v, s)))
        }),
        Some("object") => value.as_object().is_some_and(|object| {
            object.len() as u64 >= spec["minProperties"].as_u64().unwrap_or(0)
                && spec["required"].as_array().is_none_or(|keys| {
                    keys.iter()
                        .all(|k| k.as_str().is_some_and(|k| object.contains_key(k)))
                })
                && object.iter().all(|(key, v)| {
                    spec["properties"]
                        .get(key)
                        .map_or(spec["additionalProperties"] != false, |s| {
                            matches_field(v, s)
                        })
                })
        }),
        _ => false,
    };
    typed
        && spec["enum"]
            .as_array()
            .is_none_or(|values| values.contains(value))
        && value.as_f64().is_none_or(|n| {
            spec["minimum"].as_f64().is_none_or(|min| n >= min)
                && spec["maximum"].as_f64().is_none_or(|max| n <= max)
        })
}

pub(crate) fn validate(name: &str, params: &Value) -> Result<(), ApiError> {
    let fields = &command(name)
        .ok_or_else(|| ApiError::code("unknown_command"))?
        .params;
    let ok = params.as_object().is_some_and(|object| {
        fields
            .iter()
            .all(|(k, spec)| spec["required"] != true || object.contains_key(k))
            && object
                .iter()
                .all(|(k, v)| fields.get(k).is_some_and(|spec| matches_field(v, spec)))
    });
    ok.then_some(())
        .ok_or_else(|| ApiError::code("invalid_params"))
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn matches_shared_policy_fixtures() {
        let data: Value =
            serde_json::from_str(include_str!(concat!(env!("OUT_DIR"), "/policy.json"))).unwrap();
        for case in data["cases"].as_array().unwrap() {
            let scopes: Option<Vec<String>> =
                serde_json::from_value(case["scopes"].clone()).unwrap();
            let decision = decide(
                case["enabled"].as_bool().unwrap(),
                scopes.as_deref(),
                case["command"].as_str().unwrap(),
            );
            let code = match decision {
                Decision::Allow => "allow",
                Decision::Approve => "approve",
                Decision::Deny(c) => c,
            };
            assert_eq!(case["decision"], code, "{}", case["name"]);
        }
    }

    #[test]
    fn validates_schema_before_dispatch() {
        assert!(validate("app.open", &json!({"window":"library"})).is_ok());
        assert!(validate("app.open", &json!({"window":"arbitrary"})).is_err());
        assert!(validate("app.open", &json!({})).is_err());
        assert!(!matches_field(
            &json!({"x": 1}),
            &json!({"type": "object", "minProperties": 4})
        ));
        assert!(validate("capture.screenshot", &json!({"path":"/etc/passwd"})).is_err());
        assert!(
            validate(
                "capture.screenshot",
                &json!({"region":{"x":0,"y":0,"width":0,"height":10}})
            )
            .is_err()
        );
    }
}
