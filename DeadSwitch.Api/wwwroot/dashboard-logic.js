"use strict";

// The decisions behind the dashboard, kept apart from the page so that tests can run them without a
// browser. Nothing in this file touches the page. What it returns is either one of our own fixed
// texts, or a value from the server that dashboard.js then writes as plain text.

// Must be exactly CertificateReasons.NotValid on the server. A test compares the two.
const CERTIFICATE_NOT_VALID_REASON = "The certificate was not valid.";

// The groups in the order they are shown, the most urgent first.
const GROUP_CONFIRMED_DOWN = 0;
const GROUP_UNCONFIRMED_DOWN = 1;
const GROUP_UNKNOWN = 2;
const GROUP_UP = 3;

// The server only writes DOWN as the last known status after two failed checks in a row, so DOWN in
// both means confirmed. DOWN now but not before means exactly one failed check, and no alarm yet.
function getGroup(currentStatus, lastKnownStatus) {
  if (currentStatus === "DOWN" && lastKnownStatus === "DOWN") {
    return GROUP_CONFIRMED_DOWN;
  }

  if (currentStatus === "DOWN") {
    return GROUP_UNCONFIRMED_DOWN;
  }

  if (currentStatus === "UP") {
    return GROUP_UP;
  }

  // Also a value no version of the server sends, so that it is never shown as good news.
  return GROUP_UNKNOWN;
}

// The text is always set, so that the status never depends on seeing the colour. The class name is
// always one of our own, never a value from the server.
function getStatusBadge(currentStatus, lastKnownStatus) {
  const group = getGroup(currentStatus, lastKnownStatus);

  if (group === GROUP_CONFIRMED_DOWN) {
    // Says what is known. The page cannot know whether the alarm reached Discord.
    return { text: "DOWN", className: "status-down", detail: "Confirmed after 2 failed checks in a row" };
  }

  if (group === GROUP_UNCONFIRMED_DOWN) {
    return { text: "DOWN?", className: "status-pending", detail: "1 failed check, no alarm yet" };
  }

  if (group === GROUP_UP) {
    return { text: "UP", className: "status-up", detail: null };
  }

  // UNKNOWN means that no request was sent, so the last confirmed status is all that is known.
  if (lastKnownStatus === "UP") {
    return { text: "UNKNOWN", className: "status-unknown", detail: "Last confirmed: UP" };
  }

  if (lastKnownStatus === "DOWN") {
    return { text: "UNKNOWN", className: "status-unknown", detail: "Last confirmed: DOWN" };
  }

  return { text: "UNKNOWN", className: "status-unknown", detail: null };
}

// Returns a new list and leaves the server's list as it was.
function sortServices(services) {
  const sorted = services.slice();
  sorted.sort(compareServices);
  return sorted;
}

// A plain comparison of lower case names, and not localeCompare, so that the order is the same in every
// browser and in the tests. The cost is that the three Swedish letters after z are not in Swedish order.
function compareServices(first, second) {
  const firstGroup = getGroup(first.currentStatus, first.lastKnownStatus);
  const secondGroup = getGroup(second.currentStatus, second.lastKnownStatus);

  if (firstGroup !== secondGroup) {
    return firstGroup - secondGroup;
  }

  const firstName = first.name.toLowerCase();
  const secondName = second.name.toLowerCase();

  if (firstName < secondName) {
    return -1;
  }

  if (firstName > secondName) {
    return 1;
  }

  return 0;
}

// The server gives A, B, C, D or F. Each grade is compared with a fixed letter, so only those letters
// can be shown, and the class name never comes from the server.
function getGradeBox(grade) {
  if (grade === "A" || grade === "B") {
    return { text: grade, className: "grade-good" };
  }

  if (grade === "C" || grade === "D") {
    return { text: grade, className: "grade-warn" };
  }

  if (grade === "E" || grade === "F") {
    return { text: grade, className: "grade-bad" };
  }

  return { text: "-", className: "grade-none" };
}

// Null means that nothing is known about the certificate, and then the field is not shown.
function getCertificate(service) {
  // The server sets the date and the text together. The text and its limits are tested there.
  if (service.certificateNotAfterUtc !== null) {
    return {
      text: "Certificate: " + service.certificateExpiryText,
      title: "Not valid after " + formatLocalDate(service.certificateNotAfterUtc)
    };
  }

  // Only the exact reason for a failed validation. Any other reason, such as no new handshake or an
  // http address, says nothing bad about the certificate.
  if (service.certificateNotAssessedReason === CERTIFICATE_NOT_VALID_REASON) {
    return { text: "Certificate: Invalid", title: service.certificateNotAssessedReason };
  }

  if (service.certificateNotAssessedReason !== null) {
    return { text: "Certificate: Not assessed", title: service.certificateNotAssessedReason };
  }

  return null;
}

// Null hides the field. An empty list means that no header is missing, which needs no line of its own.
function getHeaderDetails(service) {
  if (service.securityNotAssessedReason !== null) {
    return "Headers not assessed: " + service.securityNotAssessedReason;
  }

  if (service.missingSecurityHeaders === null || service.missingSecurityHeaders === "") {
    return null;
  }

  return "Missing headers: " + service.missingSecurityHeaders;
}

// Null hides the field: no redirect, no finding, or a finding this page does not know.
function getRedirectText(service) {
  if (service.redirectFinding === "OTHER_HOST") {
    return "Redirects to " + service.redirectTargetHost;
  }

  if (service.redirectFinding === "SAME_HOST") {
    return "Redirects to the same host";
  }

  if (service.redirectFinding === "INTERNAL_TARGET") {
    return "Redirects to an internal address (hidden)";
  }

  if (service.redirectFinding === "UNREADABLE") {
    return "Redirect target unreadable";
  }

  return null;
}

function formatResponseTime(milliseconds) {
  if (milliseconds === null) {
    return null;
  }

  return milliseconds + " ms";
}

function formatLastCheck(time) {
  if (time === null) {
    return "Last check: Never";
  }

  return "Last check: " + formatLocalTime(time);
}

// Built from the numbers and not with the browser's own formatting, so that every browser and the
// tests give exactly 2026-11-10 13:08:59. The API sends UTC with Z, and Date gives the numbers in the
// owner's local time. Takes the text from the API or a Date.
function formatLocalTime(time) {
  const date = new Date(time);

  if (isNaN(date.getTime())) {
    return "Unknown time";
  }

  const clock = twoDigits(date.getHours()) + ":" + twoDigits(date.getMinutes()) + ":" + twoDigits(date.getSeconds());
  return formatDatePart(date) + " " + clock;
}

function formatLocalDate(time) {
  const date = new Date(time);

  if (isNaN(date.getTime())) {
    return "unknown date";
  }

  return formatDatePart(date);
}

// getMonth counts from 0, which is why 1 is added.
function formatDatePart(date) {
  return date.getFullYear() + "-" + twoDigits(date.getMonth() + 1) + "-" + twoDigits(date.getDate());
}

function twoDigits(number) {
  return String(number).padStart(2, "0");
}

// The parts of the summary, which the page shows as "2 UP | 1 DOWN | 1 waiting for confirmation".
// A group without services is left out. The class name gives the number the colour of its status, and
// is always one of our own. The word is always there as well, so the meaning never depends on the colour.
function getSummaryParts(services) {
  let up = 0;
  let confirmedDown = 0;
  let unconfirmedDown = 0;
  let unknown = 0;

  for (let i = 0; i < services.length; i++) {
    const group = getGroup(services[i].currentStatus, services[i].lastKnownStatus);

    if (group === GROUP_UP) {
      up++;
    } else if (group === GROUP_CONFIRMED_DOWN) {
      confirmedDown++;
    } else if (group === GROUP_UNCONFIRMED_DOWN) {
      unconfirmedDown++;
    } else {
      unknown++;
    }
  }

  const parts = [];

  if (up > 0) {
    parts.push({ count: up, label: "UP", className: "count-up" });
  }

  if (confirmedDown > 0) {
    parts.push({ count: confirmedDown, label: "DOWN", className: "count-down" });
  }

  if (unconfirmedDown > 0) {
    parts.push({ count: unconfirmedDown, label: "waiting for confirmation", className: "count-pending" });
  }

  if (unknown > 0) {
    parts.push({ count: unknown, label: "UNKNOWN", className: "count-unknown" });
  }

  return parts;
}
