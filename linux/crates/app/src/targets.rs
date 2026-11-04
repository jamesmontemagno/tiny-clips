use serde_json::Value;
use tinyclips_automation::ApiError;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Kind {
    Display,
    Window,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Rect {
    pub x: u32,
    pub y: u32,
    pub width: u32,
    pub height: u32,
}

pub struct Target<'a> {
    pub kind: Kind,
    pub id: &'a str,
    pub physical_size: Option<(u32, u32)>,
}

fn invalid(message: &str) -> ApiError {
    ApiError::new("invalid_params", message.to_owned())
}
fn stale() -> ApiError {
    ApiError::new(
        "not_found",
        "Target is no longer available. Refresh capture.targets.",
    )
}

/// An agent request may use a live grant or fail; it must never fall back to a picker.
pub fn resolve(
    params: &Value,
    target: Option<Target<'_>>,
    video: bool,
) -> Result<Option<Rect>, ApiError> {
    let object = params
        .as_object()
        .ok_or_else(|| invalid("Expected an object."))?;
    for key in object.keys() {
        if !(matches!(key.as_str(), "mode" | "displayId" | "windowId" | "region")
            || video && key == "studio")
        {
            return Err(invalid("Unexpected target parameter."));
        }
    }
    if let Some(studio) = object.get("studio") {
        if studio
            .as_bool()
            .ok_or_else(|| invalid("studio must be a boolean."))?
        {
            return Err(ApiError::new(
                "not_supported",
                "Studio recording is not available in this Linux preview.",
            ));
        }
    }
    let mode = match object.get("mode") {
        Some(v) => v
            .as_str()
            .ok_or_else(|| invalid("mode must be a string."))?,
        None => "screen",
    };
    let (kind, key) = match mode {
        "screen" | "region" if !object.contains_key("windowId") => (Kind::Display, "displayId"),
        "window" if !object.contains_key("displayId") => (Kind::Window, "windowId"),
        _ => return Err(invalid("Incompatible target mode or ID.")),
    };
    let supplied = object
        .get(key)
        .map(|v| {
            v.as_str()
                .filter(|s| !s.is_empty())
                .ok_or_else(|| invalid("Invalid target ID."))
        })
        .transpose()?;
    if (mode != "screen" && supplied.is_none())
        || (mode == "region") != object.contains_key("region")
    {
        return Err(invalid(
            "Use a displayId and rectangle for region, or a windowId for window.",
        ));
    }
    let live = target.ok_or_else(|| {
        ApiError::new("permission_required", "Share a source in Tiny Clips first.")
    })?;
    if live.kind != kind || supplied.is_some_and(|id| id != live.id) {
        return Err(stale());
    }
    if mode != "region" {
        return Ok(None);
    }
    let region = object["region"]
        .as_object()
        .ok_or_else(|| invalid("region must be a rectangle."))?;
    if region.len() != 4 {
        return Err(invalid("Use x, y, width and height only."));
    }
    let number = |k| {
        region
            .get(k)
            .and_then(Value::as_u64)
            .and_then(|v| u32::try_from(v).ok())
            .ok_or_else(|| invalid("Region coordinates must be nonnegative integers."))
    };
    let rect = Rect {
        x: number("x")?,
        y: number("y")?,
        width: number("width")?,
        height: number("height")?,
    };
    let (w,h) = live.physical_size.ok_or_else(|| ApiError::new("not_supported", "Cannot verify a display-physical-pixel mapping for this stream. Capture the full source instead."))?;
    if rect.width == 0
        || rect.height == 0
        || rect.x.checked_add(rect.width).is_none_or(|right| right > w)
        || rect
            .y
            .checked_add(rect.height)
            .is_none_or(|bottom| bottom > h)
    {
        return Err(invalid("Region must fit within the authorized display."));
    }
    Ok(Some(rect))
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    fn display() -> Option<Target<'static>> {
        Some(Target {
            kind: Kind::Display,
            id: "session-1",
            physical_size: Some((1600, 900)),
        })
    }
    #[test]
    fn no_silent_retargeting() {
        assert!(resolve(&json!({}), None, false).is_err());
        assert!(resolve(&json!({"displayId":"old"}), display(), false).is_err());
        assert!(
            resolve(
                &json!({"mode":"window","windowId":"session-1"}),
                display(),
                false
            )
            .is_err()
        );
        assert!(resolve(&json!({"studio":true}), display(), true).is_err());
        assert!(resolve(&json!({"displayId":"session-1"}), display(), false).is_ok());
    }
    #[test]
    fn regions_need_verifiable_physical_bounds() {
        let p = json!({"mode":"region","displayId":"session-1","region":{"x":1599,"y":899,"width":1,"height":1}});
        assert!(matches!(
            resolve(&p, display(), false),
            Ok(Some(Rect { width: 1, .. }))
        ));
        assert!(
            resolve(
                &p,
                Some(Target {
                    physical_size: None,
                    ..display().unwrap()
                }),
                false
            )
            .is_err()
        );
        let mut overflow = p;
        overflow["region"]["width"] = json!(4294967295u32);
        assert!(resolve(&overflow, display(), false).is_err());
    }
}
